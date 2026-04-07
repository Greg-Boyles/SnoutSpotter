using System.Text.Json.Serialization;

namespace SnoutSpotter.Shared.Training;

public class GpuStatus
{
    [JsonPropertyName("name")]              public string Name              { get; init; } = "";
    [JsonPropertyName("vramMb")]            public int    VramMb            { get; init; }
    [JsonPropertyName("cudaVersion")]       public string CudaVersion       { get; init; } = "";
    [JsonPropertyName("driverVersion")]     public string DriverVersion     { get; init; } = "";
    [JsonPropertyName("temperatureC")]      public int    TemperatureC      { get; init; }
    [JsonPropertyName("utilizationPercent")]public int    UtilizationPercent{ get; init; }
}
