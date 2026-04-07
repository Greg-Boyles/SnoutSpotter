namespace SnoutSpotter.TrainingAgent.Models;

public class IoTConfig
{
    public string Endpoint { get; set; } = "";
    public string ThingName { get; set; } = "";
    public string CertPath { get; set; } = "";
    public string KeyPath { get; set; } = "";
    public string RootCaPath { get; set; } = "";
}