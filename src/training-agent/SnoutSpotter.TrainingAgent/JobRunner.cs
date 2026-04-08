using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
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
    private readonly string _bucket;
    private readonly MqttManager _mqtt;
    private readonly string _thingName;
    private readonly ILogger _logger;

    private Process? _trainingProcess;

    public JobRunner(IAmazonS3 s3, string bucket, MqttManager mqtt, string thingName, ILogger logger)
    {
        _s3 = s3;
        _bucket = bucket;
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
        return RunAsync(job, ct);
    }

    /// <summary>Run from shadow desired state (legacy).</summary>
    public async Task RunAsync(TrainingJobDesired job, CancellationToken ct)
    {
        var datasetDir = Path.Combine(DataDir, job.JobId);

        try
        {
            // 1. Download ML scripts
            await PublishProgress(job.JobId, "downloading", null);
            await EnsureMlScriptsAsync(ct);

            // 2. Download and extract dataset
            _logger.LogInformation("Downloading dataset: {Key}", job.ExportS3Key);
            var zipPath = Path.Combine(DataDir, $"{job.JobId}.zip");
            Directory.CreateDirectory(DataDir);

            var transfer = new TransferUtility(_s3);
            await transfer.DownloadAsync(zipPath, _bucket, job.ExportS3Key, ct);

            Directory.CreateDirectory(datasetDir);
            ZipFile.ExtractToDirectory(zipPath, datasetDir, overwriteFiles: true);
            File.Delete(zipPath);
            _logger.LogInformation("Dataset extracted to {Dir}", datasetDir);

            // 3. Run training
            await PublishProgress(job.JobId, "training", null);

            var args = BuildTrainingArgs(job, datasetDir);
            _logger.LogInformation("Starting training: python3 {Script} {Args}",
                Path.Combine(MlScriptsDir, "train_detector.py"), args);

            var parser = new ProgressParser();
            var exitCode = await RunTrainingProcessAsync(args, parser, job.JobId, ct);

            if (ct.IsCancellationRequested)
            {
                // Upload checkpoint before reporting cancelled
                await UploadCheckpointAsync(job, datasetDir);
                await PublishProgress(job.JobId, "cancelled", null);
                return;
            }

            if (exitCode != 0)
            {
                await PublishError(job.JobId, $"Training process exited with code {exitCode}");
                return;
            }

            // 4. Upload model
            await PublishProgress(job.JobId, "uploading", null);

            var modelPath = FindBestModel(datasetDir);
            if (modelPath == null)
            {
                await PublishError(job.JobId, "No best.onnx found after training");
                return;
            }

            var version = $"v{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            var modelS3Key = $"models/dog-classifier/versions/{version}/best.onnx";
            await transfer.UploadAsync(modelPath, _bucket, modelS3Key, ct);
            _logger.LogInformation("Model uploaded to s3://{Bucket}/{Key}", _bucket, modelS3Key);

            // 5. Report final result
            var metrics = parser.GetFinalMetrics();
            var modelSize = new FileInfo(modelPath).Length / (1024.0 * 1024.0);

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
                TrainingTimeSeconds = parser.ElapsedSeconds,
                DatasetImages       = CountDatasetImages(datasetDir)
            });
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

    private async Task<int> RunTrainingProcessAsync(string args, ProgressParser parser, string jobId, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python3",
            Arguments = $"{Path.Combine(MlScriptsDir, "train_detector.py")} {args}",
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
                var progress = parser.ParseLine(line);
                if (progress != null)
                    await PublishProgress(jobId, "training", progress);
            }
        }, ct);

        // Read stderr for errors
        var stderrTask = Task.Run(async () =>
        {
            while (await _trainingProcess.StandardError.ReadLineAsync(ct) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    _logger.LogWarning("[TRAIN-ERR] {Line}", line);
            }
        }, ct);

        await _trainingProcess.WaitForExitAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);

        var exitCode = _trainingProcess.ExitCode;
        _trainingProcess = null;
        return exitCode;
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
            _logger.LogInformation("ML scripts v{Version} installed", latestVersion);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to update ML scripts from S3: {Error} — using cached version", ex.Message);
        }
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
        var imagesDir = Path.Combine(dir, "images");
        return Directory.Exists(imagesDir)
            ? Directory.GetFiles(imagesDir, "*", SearchOption.AllDirectories).Length
            : 0;
    }

    private async Task PublishProgress(string jobId, string status, TrainingProgress? progress)
    {
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

    private async Task PublishError(string jobId, string error)
    {
        _logger.LogError("Job {JobId} failed: {Error}", jobId, error);
        var message = new TrainingProgressMessage { JobId = jobId, Status = "failed", Error = error };
        await _mqtt.PublishAsync(
            $"snoutspotter/trainer/{_thingName}/progress",
            JsonSerializer.Serialize(message));
    }
}
