using System.Diagnostics;

namespace SnoutSpotter.TrainingAgent;

public record GpuStatus(
    string Name,
    int VramMb,
    int TemperatureC,
    int UtilizationPercent,
    string CudaVersion,
    string DriverVersion);

public static class GpuInfo
{
    public static GpuStatus? GetStatus()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total,temperature.gpu,utilization.gpu,driver_version --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);

            var parts = output.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 5) return null;

            // Get CUDA version separately
            var cudaVersion = GetCudaVersion();

            return new GpuStatus(
                Name: parts[0],
                VramMb: int.TryParse(parts[1], out var vram) ? vram : 0,
                TemperatureC: int.TryParse(parts[2], out var temp) ? temp : 0,
                UtilizationPercent: int.TryParse(parts[3], out var util) ? util : 0,
                CudaVersion: cudaVersion,
                DriverVersion: parts[4]);
        }
        catch
        {
            return null;
        }
    }

    private static string GetCudaVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=driver_version --format=csv,noheader",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return "unknown";

            // Parse CUDA version from the full nvidia-smi output header
            psi.Arguments = "";
            using var proc2 = Process.Start(psi);
            if (proc2 == null) return "unknown";

            var fullOutput = proc2.StandardOutput.ReadToEnd();
            proc2.WaitForExit(5000);

            var match = System.Text.RegularExpressions.Regex.Match(fullOutput, @"CUDA Version:\s+([\d.]+)");
            return match.Success ? match.Groups[1].Value : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
