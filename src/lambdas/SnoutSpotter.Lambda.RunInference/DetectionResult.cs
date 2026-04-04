namespace SnoutSpotter.Lambda.RunInference;

public class KeyframeResult
{
    public string KeyframeKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public List<DetectionBox> Detections { get; set; } = new();
}

public class DetectionBox
{
    public string Label { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public BoundingBoxData BoundingBox { get; set; } = new();
}

public class BoundingBoxData
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}
