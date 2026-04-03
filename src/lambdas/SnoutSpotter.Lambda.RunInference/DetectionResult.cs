namespace SnoutSpotter.Lambda.RunInference;

public class DetectionResult
{
    public string KeyframeKey { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public float[] BoundingBox { get; set; } = Array.Empty<float>(); // [x1, y1, x2, y2]
    public string Label { get; set; } = string.Empty; // "dog", "my_dog", "other_dog"
}