namespace OmenFlow.Core.Models;

public enum GpuMode { Hybrid = 0, Discrete = 1, Optimus = 2 }
public enum GpuPowerLevel { BasePower = 0, ExtraPower = 1, MaxPower = 2 }
public enum KeyboardType { Unknown = 0, Standard = 1, FourZoneRgb = 4, PerKeyRgb = 5 }

public record WorkerTelemetry(
    float CpuTemp,
    float GpuTemp,
    float CpuLoad,
    float GpuLoad,
    float CpuPower,
    float GpuPower,
    float RamUsedGb,
    float RamTotalGb,
    int CpuFanRpm,
    int GpuFanRpm
)
{
    public GpuMode GpuMode { get; set; } = GpuMode.Hybrid;
    public GpuPowerLevel GpuPowerLevel { get; set; } = GpuPowerLevel.BasePower;
    public KeyboardType KeyboardType { get; set; } = KeyboardType.Unknown;
    public bool BacklightOn { get; set; } = false;
    public string ZoneColors { get; set; } = ""; // base64 encoded
    public ThermalProfile ActiveProfile { get; set; } = ThermalProfile.Default;
}
