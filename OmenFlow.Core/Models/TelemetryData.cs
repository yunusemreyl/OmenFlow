namespace OmenFlow.Core.Models;

public class TelemetryData
{
    public int CpuLoadPercent { get; set; }
    public int GpuLoadPercent { get; set; }
    public int CpuPowerWatts { get; set; }
    public int GpuPowerWatts { get; set; }
    public int RamUsagePercent { get; set; }
    public double RamUsedGb { get; set; }
    public double RamTotalGb { get; set; }
    
    // Temperatures
    public int CpuTemp { get; set; }
    public int GpuTemp { get; set; }
    
    // Fans
    public int CpuFanRpm { get; set; }
    public int GpuFanRpm { get; set; }
    
    public string CurrentMode { get; set; } = "Default";
    public string CurrentFanMode { get; set; } = "Auto";
}
