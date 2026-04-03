namespace SnoutSpotter.Lambda.RunInference;

public record EventBridgeEvent<T>(string Version, string Id, string DetailType, string Source, string Account, string Time, string Region, T Detail);