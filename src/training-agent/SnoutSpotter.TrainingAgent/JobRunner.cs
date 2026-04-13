using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Transfer;
using Contracts = SnoutSpotter.Contracts;
using SnoutSpotter.Shared.Training;

namespace SnoutSpotter.TrainingAgent;

public class JobRunner
{
    private const string DataDir = "/app/data";
    private const string MlScriptsDir = "/app/ml";
    private const string ModelsDir = "/app/models";

    private readonly IAmazonS3 _s3;
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _bucket;
    private readonly string _modelsTable;
    private readonly MqttManager _mqtt;
    private readonly string _thingName;
    private readonly ILogger _logger;

    private Process? _trainingProcess;

    public string? CachedMlScriptVersion { get; private set; }

    public JobRunner(IAmazonS3 s3, IAmazonDynamoDB dynamoDb, string bucket, string modelsTable, MqttManager mqtt, string thingName, ILogger logger)
    {
        _s3 = s3;
        _dynamoDb = dynamoDb;
        _bucket = bucket;
        _modelsTable = modelsTable;
        _mqtt = mqtt;
        _thingName = thingName;
        _logger = logger;
    }

    /// <summary>Run from SQS message (new queue-based dispatch).</summary>
    public Task RunAsync(Contracts.TrainingJobMessage msg, CancellationToken ct)
    {
        var job = new TrainingJobDesired
        {
            JobId = msg.JobId,
            ExportS3Key = msg.ExportS3Key,
            Config = new TrainingJobParams
            {
                Epochs = msg.Config.Epochs,
                BatchSize = msg.Config.BatchSize,
                ImageSize = msg.Config.ImageSize,
                LearningRate = msg.Config.LearningRate,
                Workers = msg.Config.Workers,
                ModelBase = msg.Config.ModelBase,
                ResumeFrom = msg.Config.ResumeFrom
            }
        };
        return RunAsync(job, msg.JobType ?? "detector", ct);
    }

    /// <summary>Run from shadow desired state (legacy).</summary>
    public Task RunAsync(TrainingJobDesired job, CancellationToken ct) => RunAsync(job, "detector", ct);

    public async Task RunAsync(TrainingJobDesired job, string jobType, CancellationToken ct)
    {
        var isClassifier = jobType == "classifier";
        var scriptName = isClassifier ? "train_classifier.py" : "train_detector.py";
        var modelS3Prefix = isClassifier ? "models/dog-classifier" : "models/dog-detector";

        var datasetDir = Path.Combine(DataDir, job.JobId);
        var currentStage = "preparing";

        try
        {
            // 1. Download ML scripts
            await PublishProgress(job.JobId, "downloading", null);
            try
            {
                await EnsureMlScriptsAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await PublishError(job.JobId, $"Failed to prepare ML scripts: {ex.Message}", "preparing");
                return;
            }

            // 2. Download dataset
            _logger.LogInformation("Downloading dataset: {Key}", job.ExportS3Key);
            var zipPath = Path.Combine(DataDir, $"{job.JobId}.zip");
            Directory.CreateDirectory(DataDir);

            currentStage = "downloading";
            try
            {
                await DownloadWithProgressAsync(job.JobId, job.ExportS3Key, zipPath, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await PublishError(job.JobId, $"Failed to download dataset: {ex.Message}", "downloading");
                return;
            }

            // 3. Extract dataset
            currentStage = "extracting";
            try
            {
                Directory.CreateDirectory(datasetDir);
                ZipFile.ExtractToDirectory(zipPath, datasetDir, overwriteFiles: true);
                File.Delete(zipPath);
                _logger.LogInformation("Dataset extracted to {Dir}", datasetDir);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await PublishError(job.JobId, $"Failed to extract dataset: {ex.Message}", "extracting");
                return;
            }

            // 4. Scan dataset (YOLO scans all images/labels before first epoch)
            currentStage = "scanning";
            await PublishProgress(job.JobId, "scanning", null);

            // 5. Run training
            currentStage = "training";
            var args = BuildTrainingArgs(job, datasetDir);
            var scriptPath = Path.Combine(MlScriptsDir, scriptName);
            _logger.LogInformation("Starting training: python3 {Script} {Args}", scriptPath, args);

            ProgressParser? detectorParser = null;
            ClassifierProgressParser? classifierParser = null;
            Func<string, TrainingProgress?> parseLine;

            if (isClassifier)
            {
                classifierParser = new ClassifierProgressParser();
                parseLine = classifierParser.ParseLine;
            }
            else
            {
                detectorParser = new ProgressParser();
                parseLine = detectorParser.ParseLine;
            }

            var exitCode = await RunTrainingProcessAsync(scriptPath, args, parseLine, job.JobId, ct);

            if (ct.IsCancellationRequested)
            {
                // Upload checkpoint before reporting cancelled
                await UploadCheckpointAsync(job, datasetDir);
                await PublishProgress(job.JobId, "cancelled", null);
                return;
            }

            if (exitCode != 0)
            {
                await PublishError(job.JobId, $"Training process exited with code {exitCode}", "training");
                return;
            }

            // 5. Upload model
            currentStage = "uploading";
            await PublishProgress(job.JobId, "uploading", null);

            var modelPath = FindBestModel(datasetDir);
            if (modelPath == null)
            {
                await PublishError(job.JobId, "No best.onnx found after training", "uploading");
                return;
            }

            var version = $"v{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            var modelS3Key = $"{modelS3Prefix}/versions/{version}/best.onnx";
            var transfer = new TransferUtility(_s3);
            await transfer.UploadAsync(modelPath, _bucket, modelS3Key, ct);
            _logger.LogInformation("Model uploaded to s3://{Bucket}/{Key}", _bucket, modelS3Key);

            // 5b. Register model in DynamoDB
            var modelFileInfo = new FileInfo(modelPath);
            await RegisterModelAsync(jobType, version, modelS3Key, modelFileInfo.Length, job.JobId,
                isClassifier ? classifierParser : null, isClassifier ? null : detectorParser);

            // 6. Report final result
            var modelSize = modelFileInfo.Length / (1024.0 * 1024.0);
            var datasetImages = CountDatasetImages(datasetDir);

            if (isClassifier && classifierParser != null)
            {
                var metrics = classifierParser.GetFinalMetrics();
                await PublishResult(job.JobId, new TrainingResult
                {
                    ModelS3Key          = modelS3Key,
                    ModelSizeMb         = Math.Round(modelSize, 1),
                    Accuracy            = metrics.Accuracy,
                    F1Score             = metrics.F1,
                    Precision           = metrics.Precision,
                    Recall              = metrics.Recall,
                    Classes             = ["my_dog", "other_dog"],
                    TotalEpochs         = job.Config.Epochs,
                    BestEpoch           = metrics.BestEpoch,
                    TrainingTimeSeconds = classifierParser.ElapsedSeconds,
                    DatasetImages       = datasetImages
                });
            }
            else if (detectorParser != null)
            {
                var metrics = detectorParser.GetFinalMetrics();
                await PublishResult(job.JobId, new TrainingResult
                {
                    ModelS3Key          = modelS3Key,
                    ModelSizeMb         = Math.Round(modelSize, 1),
                    FinalMAP50          = metrics.MAP50,
                    FinalMAP50_95       = metrics.MAP50_95,
                    Precision           = metrics.Precision,
                    Recall              = metrics.Recall,
                    Classes             = ["my_dog", "other_dog"],
                    TotalEpochs         = job.Config.Epochs,
                    BestEpoch           = metrics.BestEpoch,
                    TrainingTimeSeconds = detectorParser.ElapsedSeconds,
                    DatasetImages       = datasetImages
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw; // let caller handle cancellation
        }
        catch (Exception ex)
        {
            _logger.LogError("Unhandled job error: {Error}", ex.Message);
            try { await PublishError(job.JobId, ex.Message, currentStage); } catch { /* best effort */ }
        }
        finally
        {
            // Cleanup dataset
            if (Directory.Exists(datasetDir))
            {
                try { Directory.Delete(datasetDir, recursive: true); }
                catch { /* best effort */ }
            }
        }
    }

    public void Cancel()
    {
        if (_trainingProcess is { HasExited: false })
        {
            _logger.LogInformation("Sending SIGTERM to training process");
            _trainingProcess.Kill(entireProcessTree: true);
        }
    }

    private static string BuildTrainingArgs(TrainingJobDesired job, string datasetDir)
    {
        var cfg = job.Config;
        var args = $"--data \"{datasetDir}\" --epochs {cfg.Epochs} --batch {cfg.BatchSize} --imgsz {cfg.ImageSize} --workers {cfg.Workers}";
        if (cfg.ResumeFrom != null)
            args += $" --resume \"{cfg.ResumeFrom}\"";
        return args;
    }

    private async Task<int> RunTrainingProcessAsync(string scriptPath, string args, Func<string, TrainingProgress?> parseLine, string jobId, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python3",
            Arguments = $"-u {scriptPath} {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _trainingProcess = Process.Start(psi);
        if (_trainingProcess == null)
            throw new InvalidOperationException("Failed to start training process");

        // Read stdout for progress
        var stdoutTask = Task.Run(async () =>
        {
            while (await _trainingProcess.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                _logger.LogInformation("[TRAIN] {Line}", line);
                var progress = parseLine(line);
                if (progress != null)
                {
                    try { await PublishProgress(jobId, "training", progress); }
                    catch (Exception ex) { _logger.LogWarning("Failed to publish progress: {Error}", ex.Message); }
                }
            }
        }, ct);

        // Read stderr — YOLO writes epoch progress here via tqdm/logging
        var stderrTask = Task.Run(async () =>
        {
            while (await _trainingProcess.StandardError.ReadLineAsync(ct) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _logger.LogWarning("[TRAIN-ERR] {Line}", line);
                    var progress = parseLine(line);
                    if (progress != null)
                    {
                        try { await PublishProgress(jobId, "training", progress); }
                        catch (Exception ex) { _logger.LogWarning("Failed to publish progress: {Error}", ex.Message); }
                    }
                }
            }
        }, ct);

        await _trainingProcess.WaitForExitAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);

        var exitCode = _trainingProcess.ExitCode;
        _trainingProcess = null;
        return exitCode;
    }

    private async Task DownloadWithProgressAsync(string jobId, string s3Key, string destPath, CancellationToken ct)
    {
        var response = await _s3.GetObjectAsync(_bucket, s3Key, ct);
        var totalBytes = response.ContentLength;

        using var responseStream = response.ResponseStream;
        await using var fileStream = File.Create(destPath);

        var buffer = new byte[81920]; // 80 KB chunks
        long downloadedBytes = 0;
        var startTime = DateTime.UtcNow;
        var lastReport = DateTime.UtcNow;
        int read;

        while ((read = await responseStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            downloadedBytes += read;

            if ((DateTime.UtcNow - lastReport).TotalSeconds >= 1)
            {
                var elapsedSec = (DateTime.UtcNow - startTime).TotalSeconds;
                var speedMbps = elapsedSec > 0 ? downloadedBytes / (1024.0 * 1024.0) / elapsedSec : 0;

                await PublishProgress(jobId, "downloading", new TrainingProgress
                {
                    DownloadBytes = downloadedBytes,
                    DownloadTotalBytes = totalBytes > 0 ? totalBytes : null,
                    DownloadSpeedMbps = Math.Round(speedMbps, 2),
                });
                lastReport = DateTime.UtcNow;
            }
        }

        var totalElapsed = (DateTime.UtcNow - startTime).TotalSeconds;
        var avgSpeedMbps = totalElapsed > 0 ? downloadedBytes / (1024.0 * 1024.0) / totalElapsed : 0;
        _logger.LogInformation("Downloaded {Bytes:N0} bytes at {Speed:F1} MB/s", downloadedBytes, avgSpeedMbps);
    }

    private async Task EnsureMlScriptsAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(MlScriptsDir);

        var localVersionPath = Path.Combine(MlScriptsDir, "version.json");
        string? localVersion = null;
        if (File.Exists(localVersionPath))
        {
            var local = JsonDocument.Parse(await File.ReadAllTextAsync(localVersionPath, ct));
            localVersion = local.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            CachedMlScriptVersion = localVersion;
        }

        // Check S3 for latest
        try
        {
            using var response = await _s3.GetObjectAsync(_bucket, "releases/ml-training/latest.json", ct);
            using var reader = new StreamReader(response.ResponseStream);
            var manifest = JsonDocument.Parse(await reader.ReadToEndAsync(ct));
            var latestVersion = manifest.RootElement.GetProperty("version").GetString();

            if (latestVersion == localVersion)
            {
                _logger.LogInformation("ML scripts v{Version} already cached", localVersion);
                CachedMlScriptVersion = localVersion;
                return;
            }

            _logger.LogInformation("Downloading ML scripts v{Version}", latestVersion);
            var tarKey = $"releases/ml-training/v{latestVersion}.tar.gz";
            var tarPath = Path.Combine(DataDir, "ml-scripts.tar.gz");
            Directory.CreateDirectory(DataDir);

            var transfer = new TransferUtility(_s3);
            await transfer.DownloadAsync(tarPath, _bucket, tarKey, ct);

            // Extract tar.gz
            var extractProc = Process.Start(new ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-xzf \"{tarPath}\" -C \"{MlScriptsDir}\"",
                UseShellExecute = false,
            });
            await extractProc!.WaitForExitAsync(ct);
            File.Delete(tarPath);

            await File.WriteAllTextAsync(localVersionPath, $"{{\"version\":\"{latestVersion}\"}}", ct);
            CachedMlScriptVersion = latestVersion;
            _logger.LogInformation("ML scripts v{Version} installed", latestVersion);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to update ML scripts from S3: {Error} — using cached version", ex.Message);
        }

        // Verify scripts are actually present
        var detectorScript = Path.Combine(MlScriptsDir, "train_detector.py");
        if (!File.Exists(detectorScript))
            throw new InvalidOperationException(
                "train_detector.py not found — ML scripts have never been downloaded. " +
                "Ensure the ml-training pipeline has run and published to S3.");
    }

    private async Task UploadCheckpointAsync(TrainingJobDesired job, string datasetDir)
    {
        var lastPt = FindFile(datasetDir, "last.pt");
        if (lastPt == null) return;

        var checkpointKey = $"training-checkpoints/{job.JobId}/last.pt";
        var transfer = new TransferUtility(_s3);
        await transfer.UploadAsync(lastPt, _bucket, checkpointKey);
        _logger.LogInformation("Checkpoint uploaded to s3://{Bucket}/{Key}", _bucket, checkpointKey);
    }

    private static string? FindBestModel(string datasetDir)
    {
        // YOLO exports best.onnx in the runs directory
        return Directory.GetFiles(datasetDir, "best.onnx", SearchOption.AllDirectories).FirstOrDefault()
            ?? Directory.GetFiles(datasetDir, "best.pt", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string? FindFile(string dir, string name)
        => Directory.GetFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();

    private static int CountDatasetImages(string dir)
    {
        // YOLO detection format: images/train/*.jpg + images/val/*.jpg
        var imagesDir = Path.Combine(dir, "images");
        if (Directory.Exists(imagesDir))
            return Directory.GetFiles(imagesDir, "*", SearchOption.AllDirectories).Length;

        // Classification format: train/my_dog/*.jpg + val/other_dog/*.jpg
        var trainDir = Path.Combine(dir, "train");
        var valDir = Path.Combine(dir, "val");
        var count = 0;
        if (Directory.Exists(trainDir))
            count += Directory.GetFiles(trainDir, "*", SearchOption.AllDirectories).Length;
        if (Directory.Exists(valDir))
            count += Directory.GetFiles(valDir, "*", SearchOption.AllDirectories).Length;
        return count;
    }

    private async Task RegisterModelAsync(string modelType, string version, string s3Key, long sizeBytes,
        string trainingJobId, ClassifierProgressParser? classifierParser, ProgressParser? detectorParser)
    {
        try
        {
            var item = new Dictionary<string, AttributeValue>
            {
                ["model_id"] = new() { S = $"{modelType}#{version}" },
                ["model_type"] = new() { S = modelType },
                ["version"] = new() { S = version },
                ["s3_key"] = new() { S = s3Key },
                ["size_bytes"] = new() { N = sizeBytes.ToString() },
                ["status"] = new() { S = "uploaded" },
                ["created_at"] = new() { S = DateTime.UtcNow.ToString("O") },
                ["source"] = new() { S = "training" },
                ["training_job_id"] = new() { S = trainingJobId },
            };

            var metrics = new Dictionary<string, AttributeValue>();
            if (classifierParser != null)
            {
                var m = classifierParser.GetFinalMetrics();
                metrics["accuracy"] = new() { N = m.Accuracy.ToString("G", CultureInfo.InvariantCulture) };
                metrics["f1_score"] = new() { N = m.F1.ToString("G", CultureInfo.InvariantCulture) };
                metrics["precision"] = new() { N = m.Precision.ToString("G", CultureInfo.InvariantCulture) };
                metrics["recall"] = new() { N = m.Recall.ToString("G", CultureInfo.InvariantCulture) };
            }
            else if (detectorParser != null)
            {
                var m = detectorParser.GetFinalMetrics();
                metrics["final_mAP50"] = new() { N = m.MAP50.ToString("G", CultureInfo.InvariantCulture) };
                metrics["precision"] = new() { N = m.Precision.ToString("G", CultureInfo.InvariantCulture) };
                metrics["recall"] = new() { N = m.Recall.ToString("G", CultureInfo.InvariantCulture) };
            }

            if (metrics.Count > 0)
                item["metrics"] = new() { M = metrics };

            await _dynamoDb.PutItemAsync(_modelsTable, item);
            _logger.LogInformation("Model registered in DynamoDB: {Type}#{Version}", modelType, version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to register model in DynamoDB: {Error} — model still uploaded to S3", ex.Message);
        }
    }

    private async Task PublishProgress(string jobId, string status, TrainingProgress? progress)
    {
        _logger.LogInformation("Publishing progress: status={Status} epoch={Epoch}", status, progress?.Epoch);
        var message = new TrainingProgressMessage { JobId = jobId, Status = status, Progress = progress };
        await _mqtt.PublishAsync(
            $"snoutspotter/trainer/{_thingName}/progress",
            JsonSerializer.Serialize(message));
    }

    private async Task PublishResult(string jobId, TrainingResult result)
    {
        var message = new TrainingProgressMessage
        {
            JobId    = jobId,
            Status   = "complete",
            Progress = new TrainingProgress { Epoch = result.TotalEpochs, TotalEpochs = result.TotalEpochs },
            Result   = result
        };
        await _mqtt.PublishAsync(
            $"snoutspotter/trainer/{_thingName}/progress",
            JsonSerializer.Serialize(message));
    }

    private async Task PublishError(string jobId, string error, string stage)
    {
        _logger.LogError("Job {JobId} failed during {Stage}: {Error}", jobId, stage, error);
        var message = new TrainingProgressMessage { JobId = jobId, Status = "failed", Error = error, FailedStage = stage };
        await _mqtt.PublishAsync(
            $"snoutspotter/trainer/{_thingName}/progress",
            JsonSerializer.Serialize(message));
    }
}
