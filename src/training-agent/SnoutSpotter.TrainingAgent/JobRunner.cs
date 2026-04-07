using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Transfer;
using SnoutSpotter.TrainingAgent.Models;

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

    public async Task RunAsync(TrainingJobConfig job, CancellationToken ct)
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

            await PublishResult(job.JobId, new TrainingResult(
                ModelS3Key: modelS3Key,
                ModelSizeMb: Math.Round(modelSize, 1),
                FinalMAP50: metrics.MAP50,
                FinalMAP50_95: metrics.MAP50_95,
                Precision: metrics.Precision,
                Recall: metrics.Recall,
                Classes: ["my_dog", "other_dog"],
                TotalEpochs: job.Epochs,
                BestEpoch: metrics.BestEpoch,
                TrainingTimeSeconds: parser.ElapsedSeconds,
                DatasetImages: CountDatasetImages(datasetDir)));
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

    private string BuildTrainingArgs(TrainingJobConfig job, string datasetDir)
    {
        var args = $"--data \"{datasetDir}\" --epochs {job.Epochs} --batch {job.BatchSize} --imgsz {job.ImageSize} --workers {job.Workers}";
        if (job.ResumeFrom != null)
            args += $" --resume \"{job.ResumeFrom}\"";
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

    private async Task UploadCheckpointAsync(TrainingJobConfig job, string datasetDir)
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
        var payload = new Dictionary<string, object?> { ["job_id"] = jobId, ["status"] = status };
        if (progress != null)
        {
            payload["progress"] = new
            {
                epoch = progress.Epoch,
                total_epochs = progress.TotalEpochs,
                train_loss = progress.TrainLoss,
                val_loss = progress.ValLoss,
                mAP50 = progress.MAP50,
                mAP50_95 = progress.MAP50_95,
                best_mAP50 = progress.BestMAP50,
                elapsed_seconds = progress.ElapsedSeconds,
                eta_seconds = progress.EtaSeconds,
                gpu_util_percent = progress.GpuUtilPercent,
                gpu_temp_c = progress.GpuTempC
            };
        }

        await _mqtt.PublishAsync(
            $"snoutspotter/trainer/{_thingName}/progress",
            JsonSerializer.Serialize(payload));
    }

    private async Task PublishResult(string jobId, TrainingResult result)
    {
        var payload = new
        {
            job_id = jobId,
            status = "complete",
            progress = new { epoch = result.TotalEpochs, total_epochs = result.TotalEpochs },
            result = new
            {
                model_s3_key = result.ModelS3Key,
                model_size_mb = result.ModelSizeMb,
                final_mAP50 = result.FinalMAP50,
                final_mAP50_95 = result.FinalMAP50_95,
                precision = result.Precision,
                recall = result.Recall,
                classes = result.Classes,
                total_epochs = result.TotalEpochs,
                best_epoch = result.BestEpoch,
                training_time_seconds = result.TrainingTimeSeconds,
                dataset_images = result.DatasetImages
            }
        };

        await _mqtt.PublishAsync(
            $"snoutspotter/trainer/{_thingName}/progress",
            JsonSerializer.Serialize(payload));
    }

    private async Task PublishError(string jobId, string error)
    {
        _logger.LogError("Job {JobId} failed: {Error}", jobId, error);
        await _mqtt.PublishAsync(
            $"snoutspotter/trainer/{_thingName}/progress",
            JsonSerializer.Serialize(new { job_id = jobId, status = "failed", error }));
    }
}
