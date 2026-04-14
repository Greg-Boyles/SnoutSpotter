namespace SnoutSpotter.Api.Models;

public record TriggerExportRequest(
    int? MaxPerClass = null,
    bool IncludeBackground = true,
    float BackgroundRatio = 1.0f,
    string ExportType = "detection",
    float CropPadding = 0.1f,
    bool MergeClasses = false);