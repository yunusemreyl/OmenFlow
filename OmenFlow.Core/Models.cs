using System;
using System.Collections.Generic;

namespace OmenFlow.Core.Models;

public readonly record struct FanCurvePoint(int TemperatureCelsius, int FanSpeedPercent);

public enum FanTarget
{
    Cpu,
    Gpu,
    Both
}

public record FanCurve(FanTarget Target, IReadOnlyList<FanCurvePoint> Points);

public enum ThermalProfile : byte
{
    Quiet = 0x50,
    Default = 0x30,
    Performance = 0x31
}

public enum KeyboardZone
{
    Zone0 = 0,
    Zone1 = 1,
    Zone2 = 2,
    Zone3 = 3
}

public enum GpuMuxMode
{
    Discrete = 0x01,
    Hybrid = 0x02
}

public record GpuPowerPreset(byte PresetId, string Name, int MaxWattage);

public readonly record struct RgbColor(byte R, byte G, byte B);

/// <summary>
/// Message payload for telemetry updates sent from Hardware to UI.
/// </summary>
public record HardwareTelemetryMessage(
    int CpuFanRpm, 
    int GpuFanRpm, 
    int CpuTempCelsius, 
    int GpuTempCelsius, 
    int CpuLoadPercent,
    int GpuLoadPercent,
    float CpuPowerWatts,
    float GpuPowerWatts,
    int RamUsagePercent,
    double RamUsedGb,
    double RamTotalGb,
    ThermalProfile ActiveProfile
);

public enum DeviceFamily
{
    Unknown,
    OmenLegacy,
    OmenV1,
    Victus,
    VictusS
}

public record BoardConfiguration(
    string BoardId, 
    DeviceFamily Family,
    bool HasEcThermalOffset, 
    int MaxFanLevel = 55,
    bool SupportsDetailedPowerLimits = false,
    bool UseSimplifiedPerformanceMode = true
);

public record FanPreset(
    string Id,
    string DisplayName,
    bool IsBuiltIn,
    bool IsMaxMode,
    FanCurve? Curve
);

public enum BatteryCareMode : byte
{
    Disabled = 0x00,
    Enabled = 0x01
}
