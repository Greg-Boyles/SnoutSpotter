using System.Globalization;
using System.Text.RegularExpressions;
using SnoutSpotter.Shared.Training;

namespace SnoutSpotter.TrainingAgent;

/// <summary>
/// Parses train_classifier.py stdout for per-epoch metrics and intra-epoch tqdm progress.
/// Epoch summary:  EPOCH 5/50 train_loss=0.234 val_loss=0.189 accuracy=0.923 f1=0.891 precision=0.867 recall=0.912 elapsed=120s eta=300s
/// Intra-epoch:    TRAIN 5/50:  45%|████      | 12/27
///                 VAL 5/50:  80%|████████  | 8/10
/// </summary>
public class ClassifierProgressParser
{
    private static readonly Regex EpochRegex = new(
        @"^EPOCH\s+(\d+)/(\d+)\s+(.+)",
        RegexOptions.Compiled);

    // Match: TRAIN 5/50:  45%|  or  VAL 5/50:  80%|
    private static readonly Regex StageRegex = new(
        @"^(TRAIN|VAL)\s+(\d+)/(\d+):\s+(\d+)%\|",
        RegexOptions.Compiled);

    private static readonly Regex KeyValueRegex = new(
        @"(\w+)=([\d.]+)",
        RegexOptions.Compiled);

    // Strip ANSI/VT100 escape sequences (tqdm uses \r and cursor movement)
    private static readonly Regex AnsiRegex = new(
        @"\x1b\[[0-9;]*[A-Za-z]|\x1b\[[A-Za-z]|\x1b[A-Za-z]",
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
    private int _lastPublishedPercent = -1;
    private string _lastStage = "train";
    private readonly DateTime _startTime = DateTime.UtcNow;

    public TrainingProgress? ParseLine(string line)
    {
        line = AnsiRegex.Replace(line, "");

        // Check for intra-epoch tqdm progress (TRAIN/VAL bars)
        var stageMatch = StageRegex.Match(line);
        if (stageMatch.Success)
        {
            var stage = stageMatch.Groups[1].Value;
            var epoch = int.Parse(stageMatch.Groups[2].Value);
            _totalEpochs = int.Parse(stageMatch.Groups[3].Value);
            var pct = int.Parse(stageMatch.Groups[4].Value);

            // TRAIN = 0-50%, VAL = 50-100% of the epoch
            var epochProgress = stage == "TRAIN" ? pct / 2 : 50 + pct / 2;

            var epochChanged = epoch != _lastEpoch;
            var stageChanged = stage.ToLowerInvariant() != _lastStage;
            var significantProgress = !epochChanged && !stageChanged && (epochProgress - _lastPublishedPercent >= 10);

            if (!epochChanged && !stageChanged && !significantProgress) return null;

            if (epochChanged) _lastEpoch = epoch;
            _lastStage = stage.ToLowerInvariant();
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
                ValLoss        = _lastValLoss,
                Accuracy       = _lastAccuracy,
                F1Score        = _lastF1,
                ElapsedSeconds = elapsed,
                EtaSeconds     = eta,
                GpuUtilPercent = gpu?.UtilizationPercent,
                GpuTempC       = gpu?.TemperatureC
            };
        }

        // Check for epoch summary line
        var epochMatch = EpochRegex.Match(line);
        if (!epochMatch.Success) return null;

        _lastEpoch = int.Parse(epochMatch.Groups[1].Value);
        _totalEpochs = int.Parse(epochMatch.Groups[2].Value);
        _lastPublishedPercent = -1;

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

        var elapsed2 = (long)(DateTime.UtcNow - _startTime).TotalSeconds;
        var perEpoch = _lastEpoch > 0 ? elapsed2 / _lastEpoch : 0;
        var eta2 = perEpoch * (_totalEpochs - _lastEpoch);
        var gpu2 = GpuInfo.GetStatus();

        return new TrainingProgress
        {
            Epoch          = _lastEpoch,
            TotalEpochs    = _totalEpochs,
            EpochProgress  = 100,
            TrainLoss      = _lastTrainLoss,
            ValLoss        = _lastValLoss,
            Accuracy       = _lastAccuracy,
            F1Score        = _lastF1,
            ElapsedSeconds = elapsed2,
            EtaSeconds     = eta2,
            GpuUtilPercent = gpu2?.UtilizationPercent,
            GpuTempC       = gpu2?.TemperatureC
        };
    }

    public (double Accuracy, double F1, double Precision, double Recall, int BestEpoch) GetFinalMetrics()
        => (_lastAccuracy, _lastF1, _lastPrecision, _lastRecall, _bestEpoch);

    public long ElapsedSeconds => (long)(DateTime.UtcNow - _startTime).TotalSeconds;
}
