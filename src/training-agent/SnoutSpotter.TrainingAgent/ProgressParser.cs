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

    // Match tqdm percentage:  :  45%|
    private static readonly Regex PercentRegex = new(
        @":\s+(\d+)%\|",
        RegexOptions.Compiled);

    // Strip ANSI/VT100 escape sequences (e.g. \x1b[K from tqdm progress bars)
    private static readonly Regex AnsiRegex = new(
        @"\x1b\[[0-9;]*[A-Za-z]|\x1b\[[A-Za-z]|\x1b[A-Za-z]",
        RegexOptions.Compiled);

    private int _lastEpoch;
    private int _totalEpochs;
    private double _lastTrainLoss;
    private double _bestMAP50;
    private int _lastPublishedPercent = -1;
    private readonly DateTime _startTime = DateTime.UtcNow;

    // Last parsed validation metrics (set when MetricsRegex matches)
    private double _lastPrecision;
    private double _lastRecall;
    private double _lastMAP50;
    private double _lastMAP50_95;

    public TrainingProgress? ParseLine(string line)
    {
        line = AnsiRegex.Replace(line, "");
        var epochMatch = EpochRegex.Match(line);
        if (epochMatch.Success)
        {
            var epoch = int.Parse(epochMatch.Groups[1].Value);
            _totalEpochs = int.Parse(epochMatch.Groups[2].Value);
            _lastTrainLoss = double.Parse(epochMatch.Groups[3].Value);

            // Extract tqdm percentage (0-100) from the line
            int epochProgress = 0;
            var pctMatch = PercentRegex.Match(line);
            if (pctMatch.Success)
                epochProgress = int.Parse(pctMatch.Groups[1].Value);

            // Publish on epoch change OR when percentage jumps by >=10 within same epoch
            var epochChanged = epoch != _lastEpoch;
            var significantProgress = !epochChanged && (epochProgress - _lastPublishedPercent >= 10);

            if (!epochChanged && !significantProgress) return null;

            if (epochChanged)
                _lastEpoch = epoch;
            _lastPublishedPercent = epochProgress;

            var elapsed = (long)(DateTime.UtcNow - _startTime).TotalSeconds;
            var completedFraction = (_lastEpoch - 1 + epochProgress / 100.0) / _totalEpochs;
            var eta = completedFraction > 0 ? (long)(elapsed / completedFraction * (1 - completedFraction)) : 0;
            var gpu = GpuInfo.GetStatus();

            return new TrainingProgress
            {
                Epoch          = _lastEpoch,
                TotalEpochs    = _totalEpochs,
                EpochProgress  = epochProgress,
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
