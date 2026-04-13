namespace SnoutSpotter.Contracts;

/// <summary>
/// Server-side settings keys, defaults, and validation.
/// Stored in DynamoDB snout-spotter-settings table.
/// Shared between API (read/write) and all Lambdas (read).
/// </summary>
public static class ServerSettings
{
    // Ingest
    public const string IngestKeyframeInterval = "ingest.keyframe_interval_seconds";
    public const string IngestJpegQuality = "ingest.jpeg_quality";

    // Inference
    public const string InferenceConfidenceThreshold = "inference.confidence_threshold";
    public const string InferenceInputSize = "inference.input_size";
    public const string InferencePipelineMode = "inference.pipeline_mode";
    public const string InferenceClassifierConfidenceThreshold = "inference.classifier_confidence_threshold";
    public const string InferenceClassifierInputSize = "inference.classifier_input_size";
    public const string InferenceCropPaddingRatio = "inference.crop_padding_ratio";

    // AutoLabel
    public const string AutoLabelConfidenceThreshold = "autolabel.confidence_threshold";
    public const string AutoLabelModelKey = "autolabel.model_key";

    // Export
    public const string ExportTrainSplitRatio = "export.train_split_ratio";
    public const string ExportMaxParallelDownloads = "export.max_parallel_downloads";

    private static readonly string[] PipelineModeOptions = { "single", "two_stage" };

    private static readonly string[] AutoLabelModelOptions =
    {
        "models/yolov8n.onnx",
        "models/yolov8s.onnx",
        "models/yolov8m.onnx",
    };

    public static readonly Dictionary<string, SettingSpec> All = new()
    {
        [IngestKeyframeInterval]        = new("Keyframe interval",       "5",    "int",   1,   30,   "Extract 1 frame every N seconds from clips"),
        [IngestJpegQuality]             = new("JPEG quality",            "2",    "int",   1,   31,   "FFmpeg quality (1=best, 31=worst)"),
        [InferenceConfidenceThreshold]  = new("Confidence threshold",    "0.4",  "float", 0.1, 0.95, "Minimum detection confidence for RunInference"),
        [InferenceInputSize]            = new("Input size",              "640",  "int",   320, 1280, "YOLO model input resolution (pixels)"),
        [InferencePipelineMode]         = new("Pipeline mode",           "single", "select", 0, 0, "single = two-class YOLO, two_stage = COCO detector + classifier", PipelineModeOptions),
        [InferenceClassifierConfidenceThreshold] = new("Classifier confidence", "0.5", "float", 0.1, 0.95, "Minimum classifier confidence for my_dog/other_dog"),
        [InferenceClassifierInputSize]  = new("Classifier input size",   "224",  "int",   128, 512, "Classifier model input resolution (pixels)"),
        [InferenceCropPaddingRatio]     = new("Crop padding ratio",      "0.1",  "float", 0.0, 0.5, "Extra padding around dog bounding box before classification"),
        [AutoLabelConfidenceThreshold]  = new("Confidence threshold",    "0.25", "float", 0.1, 0.95, "Minimum confidence for COCO dog detection"),
        [AutoLabelModelKey]             = new("Detection model",         "models/yolov8m.onnx", "select", 0, 0, "COCO-pretrained YOLOv8 model used for auto-labelling. Larger = more accurate but slower.", AutoLabelModelOptions),
        [ExportTrainSplitRatio]         = new("Train split ratio",       "0.8",  "float", 0.5, 0.95, "Fraction of images for training (rest = validation)"),
        [ExportMaxParallelDownloads]    = new("Max parallel downloads",  "20",   "int",   1,   50,   "Concurrent S3 downloads during export"),
    };

    public static string GetDefault(string key) => All.TryGetValue(key, out var spec) ? spec.Default : "";
}

public record SettingSpec(string Label, string Default, string Type, double Min, double Max, string Description, string[]? Options = null);
