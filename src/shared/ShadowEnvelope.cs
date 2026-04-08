using System.Text.Json.Serialization;

namespace SnoutSpotter.Shared.Training;

/// <summary>Wraps a desired state update: { "state": { "desired": T } }</summary>
public class ShadowDesiredUpdate<T>
{
    [JsonPropertyName("state")]
    public DesiredWrapper<T> State { get; init; } = new();

    public static ShadowDesiredUpdate<T> From(T desired) =>
        new() { State = new DesiredWrapper<T> { Desired = desired } };
}

public class DesiredWrapper<T>
{
    [JsonPropertyName("desired")]
    public T? Desired { get; init; }
}

/// <summary>Wraps a reported state update: { "state": { "reported": T } }</summary>
public class ShadowReportedUpdate<T>
{
    [JsonPropertyName("state")]
    public ReportedWrapper<T> State { get; init; } = new();

    public static ShadowReportedUpdate<T> From(T reported) =>
        new() { State = new ReportedWrapper<T> { Reported = reported } };
}

public class ReportedWrapper<T>
{
    [JsonPropertyName("reported")]
    public T? Reported { get; init; }
}

/// <summary>Deserialises an incoming shadow delta: { "state": T }</summary>
public class ShadowDeltaMessage<T>
{
    [JsonPropertyName("state")]
    public T? State { get; init; }
}
