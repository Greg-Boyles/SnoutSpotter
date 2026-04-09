using System.Text.RegularExpressions;
using SnoutSpotter.Shared.Training;

namespace SnoutSpotter.TrainingAgent;

/// <summary>
/// Parses YOLO (ultralytics) stdout for per-epoch training metrics.
/// </summary>
public class ProgressParser
{
    // Match:       45/100      4.8G    0.02341    0.01234    0.00891         12        640
    private static readonly Regex EpochRegex = new(
        @"^\s+(\d+)/(\d+)\s+[\d.]+G\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)",
        RegexOptions.Compiled);

    // Match:                    all        250        312      0.879      0.931      0.912      0.742
    private static readonly Regex MetricsRegex = new(
        @"^\s+all\s+\d+\s+\d+\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)",
        RegexOptions.Compiled);

    private int _lastEpoch;
    private int _totalEpochs;
    private double _lastTrainLoss;
    private double _bestMAP50;
    private readonly DateTime _startTime = DateTime.UtcNow;

    // Last parsed validation metrics (set when MetricsRegex matches)
    private double _lastPrecision;
    private double _lastRecall;
    private double _lastMAP50;
    private double _lastMAP50_95;

    public TrainingProgress? ParseLine(string line)
    {
        var epochMatch = EpochRegex.Match(line);
        if (epochMatch.Success)
        {
            var epoch = int.Parse(epochMatch.Groups[1].Value);
            _totalEpochs = int.Parse(epochMatch.Groups[2].Value);
            _lastTrainLoss = double.Parse(epochMatch.Groups[3].Value);

            // Publish as soon as the epoch number changes — don't wait for the
            // val metrics line, which may be delayed by Python's output buffering.
            // A second update with mAP50 follows when MetricsRegex matches.
            if (epoch == _lastEpoch) return null;
            _lastEpoch = epoch;

            var elapsed = (long)(DateTime.UtcNow - _startTime).TotalSeconds;
            var perEpoch = _lastEpoch > 0 ? elapsed / _lastEpoch : 0;
            var eta = perEpoch * (_totalEpochs - _lastEpoch);
            var gpu = GpuInfo.GetStatus();

            return new TrainingProgress
            {
                Epoch          = _lastEpoch,
                TotalEpochs    = _totalEpochs,
                TrainLoss      = _lastTrainLoss,
                MAP50          = _lastMAP50 > 0 ? _lastMAP50 : null,
                MAP50_95       = _lastMAP50_95 > 0 ? _lastMAP50_95 : null,
                BestMAP50      = _bestMAP50 > 0 ? _bestMAP50 : null,
                ElapsedSeconds = elapsed,
                EtaSeconds     = eta,
                GpuUtilPercent = gpu?.UtilizationPercent,
                GpuTempC       = gpu?.TemperatureC
            };
        }

        var metricsMatch = MetricsRegex.Match(line);
        if (metricsMatch.Success && _lastEpoch > 0)
        {
            _lastPrecision = double.Parse(metricsMatch.Groups[1].Value);
            _lastRecall = double.Parse(metricsMatch.Groups[2].Value);
            _lastMAP50 = double.Parse(metricsMatch.Groups[3].Value);
            _lastMAP50_95 = double.Parse(metricsMatch.Groups[4].Value);

            if (_lastMAP50 > _bestMAP50)
                _bestMAP50 = _lastMAP50;

            var elapsed = (long)(DateTime.UtcNow - _startTime).TotalSeconds;
            var perEpoch = _lastEpoch > 0 ? elapsed / _lastEpoch : 0;
            var eta = perEpoch * (_totalEpochs - _lastEpoch);
            var gpu = GpuInfo.GetStatus();

            return new TrainingProgress
            {
                Epoch          = _lastEpoch,
                TotalEpochs    = _totalEpochs,
                TrainLoss      = _lastTrainLoss,
                MAP50          = _lastMAP50,
                MAP50_95       = _lastMAP50_95,
                BestMAP50      = _bestMAP50,
                ElapsedSeconds = elapsed,
                EtaSeconds     = eta,
                GpuUtilPercent = gpu?.UtilizationPercent,
                GpuTempC       = gpu?.TemperatureC
            };
        }

        return null;
    }

    public (double Precision, double Recall, double MAP50, double MAP50_95, int BestEpoch, double BestMAP50) GetFinalMetrics()
        => (_lastPrecision, _lastRecall, _lastMAP50, _lastMAP50_95, _lastEpoch, _bestMAP50);

    public long ElapsedSeconds => (long)(DateTime.UtcNow - _startTime).TotalSeconds;
}
