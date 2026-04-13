using System.Globalization;
using System.Text.RegularExpressions;
using SnoutSpotter.Shared.Training;

namespace SnoutSpotter.TrainingAgent;

/// <summary>
/// Parses train_classifier.py stdout for per-epoch metrics.
/// Expected format: EPOCH 5/50 train_loss=0.234 val_loss=0.189 accuracy=0.923 f1=0.891 precision=0.867 recall=0.912 elapsed=120s eta=300s
/// </summary>
public class ClassifierProgressParser
{
    private static readonly Regex EpochRegex = new(
        @"^EPOCH\s+(\d+)/(\d+)\s+(.+)",
        RegexOptions.Compiled);

    private static readonly Regex KeyValueRegex = new(
        @"(\w+)=([\d.]+)",
        RegexOptions.Compiled);

    private int _lastEpoch;
    private int _totalEpochs;
    private double _lastTrainLoss;
    private double _lastValLoss;
    private double _lastAccuracy;
    private double _lastF1;
    private double _lastPrecision;
    private double _lastRecall;
    private double _bestAccuracy;
    private int _bestEpoch;
    private readonly DateTime _startTime = DateTime.UtcNow;

    public TrainingProgress? ParseLine(string line)
    {
        var epochMatch = EpochRegex.Match(line);
        if (!epochMatch.Success) return null;

        _lastEpoch = int.Parse(epochMatch.Groups[1].Value);
        _totalEpochs = int.Parse(epochMatch.Groups[2].Value);

        var kvPart = epochMatch.Groups[3].Value;
        foreach (Match kv in KeyValueRegex.Matches(kvPart))
        {
            var key = kv.Groups[1].Value;
            var val = double.Parse(kv.Groups[2].Value, CultureInfo.InvariantCulture);
            switch (key)
            {
                case "train_loss": _lastTrainLoss = val; break;
                case "val_loss":   _lastValLoss = val; break;
                case "accuracy":   _lastAccuracy = val; break;
                case "f1":         _lastF1 = val; break;
                case "precision":  _lastPrecision = val; break;
                case "recall":     _lastRecall = val; break;
            }
        }

        if (_lastAccuracy > _bestAccuracy)
        {
            _bestAccuracy = _lastAccuracy;
            _bestEpoch = _lastEpoch;
        }

        var elapsed = (long)(DateTime.UtcNow - _startTime).TotalSeconds;
        var perEpoch = _lastEpoch > 0 ? elapsed / _lastEpoch : 0;
        var eta = perEpoch * (_totalEpochs - _lastEpoch);
        var gpu = GpuInfo.GetStatus();

        return new TrainingProgress
        {
            Epoch          = _lastEpoch,
            TotalEpochs    = _totalEpochs,
            TrainLoss      = _lastTrainLoss,
            ValLoss        = _lastValLoss,
            Accuracy       = _lastAccuracy,
            F1Score        = _lastF1,
            ElapsedSeconds = elapsed,
            EtaSeconds     = eta,
            GpuUtilPercent = gpu?.UtilizationPercent,
            GpuTempC       = gpu?.TemperatureC
        };
    }

    public (double Accuracy, double F1, double Precision, double Recall, int BestEpoch) GetFinalMetrics()
        => (_lastAccuracy, _lastF1, _lastPrecision, _lastRecall, _bestEpoch);

    public long ElapsedSeconds => (long)(DateTime.UtcNow - _startTime).TotalSeconds;
}
