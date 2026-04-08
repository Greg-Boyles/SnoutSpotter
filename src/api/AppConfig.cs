namespace SnoutSpotter.Api;

public class AppConfig
{
    public string BucketName { get; set; } = "";
    public string ClipsTable { get; set; } = "snout-spotter-clips";
    public string CommandsTable { get; set; } = "snout-spotter-commands";
    public string LabelsTable { get; set; } = "snout-spotter-labels";
    public string ExportsTable { get; set; } = "snout-spotter-exports";
    public string IoTThingGroup { get; set; } = "snoutspotter-pis";
    public string PiLogGroup { get; set; } = "/snoutspotter/pi-logs";
    public string AutoLabelFunction { get; set; } = "snout-spotter-auto-label";
    public string ExportDatasetFunction { get; set; } = "snout-spotter-export-dataset";
    public string InferenceFunction { get; set; } = "snout-spotter-run-inference";
    public string BackfillQueueUrl { get; set; } = "";
    public string RerunInferenceQueueUrl { get; set; } = "";
    public string TrainingJobsTable { get; set; } = "snout-spotter-training-jobs";
    public string TrainerThingGroup { get; set; } = "snoutspotter-trainers";
    public string OktaIssuer { get; set; } = "";
    public string AllowedOrigin { get; set; } = "";
}
