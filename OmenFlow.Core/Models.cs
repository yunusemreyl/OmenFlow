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
    /// <summary>OMEN 2023+ models using 0-100% percentage fan scale (MaxFanLevel=100).</summary>
    OmenV2,
    Victus,
    VictusS
}


public record BoardConfiguration(
    string BoardId, 
    DeviceFamily Family,
    bool HasEcThermalOffset, 
    int MaxFanLevel = 55,
    bool SupportsDetailedPowerLimits = false,
    bool UseSimplifiedPerformanceMode = true,
    /// <summary>Whether custom fan curves (EC temperature-based) are safe on this model.</summary>
    bool SupportsFanCurves = true,
    /// <summary>Whether direct EC fan writes are safe (false = WMI-only model).</summary>
    bool SupportsFanControlEc = true,
    /// <summary>Number of physical fans (1 = single fan, 2 = CPU+GPU separate).</summary>
    int FanCount = 2,
    /// <summary>Desktop units should never have fan writes issued.</summary>
    bool IsDesktop = false
);

/// <summary>
/// Hysteresis settings for fan curve evaluation.
/// Prevents fan oscillation by requiring sustained temp changes before altering fan speed.
/// Mirrors OmenCore's FanHysteresisSettings.
/// </summary>
public record FanHysteresisSettings
{
    /// <summary>Seconds temp must stay above threshold before ramping UP (prevents fan surge on brief spikes).</summary>
    public double RiseDebounceSeconds { get; init; } = 5.0;
    /// <summary>Seconds temp must stay below threshold before ramping DOWN (prevents fan yo-yo).</summary>
    public double DropDebounceSeconds { get; init; } = 15.0;
    /// <summary>Minimum fan speed change (%) before a write is issued (reduces EC traffic).</summary>
    public int MinDeltaPercent { get; init; } = 3;
    /// <summary>°C below thermal protection threshold before protection releases.</summary>
    public double ThermalReleaseHysteresis { get; init; } = 10.0;
}

/// <summary>
/// Timestamped record of a fan command for diagnostics and field reports.
/// </summary>
public record FanCommandEntry(
    DateTime TimestampUtc,
    string Command,
    string Target,
    bool Success,
    string Backend,
    int FanMode,
    bool CurveActive,
    bool ThermalProtectionActive,
    int CpuTempC,
    int GpuTempC,
    int CpuFanRpm,
    int GpuFanRpm,
    string Details = ""
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
