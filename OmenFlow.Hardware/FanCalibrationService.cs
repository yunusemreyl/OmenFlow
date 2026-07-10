using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

/// <summary>
/// Provides model-specific RPM ↔ fan-percent calibration data.
///
/// Mirrors OmenCore's FanCalibrationService approach:
/// - Each model family has a characteristic RPM range for CPU and GPU fans.
/// - A calibration profile maps LUT entries (0-MaxFanLevel) to expected RPM.
/// - The profile is used by FanVerificationService and telemetry display.
/// - Supports persisted per-device calibration override (saved to ProgramData).
///
/// OmenCore uses measured data from field reports. We seed conservative estimates
/// and allow field calibration to narrow them over time.
/// </summary>
public class FanCalibrationService
{
    private readonly BoardConfiguration _boardConfig;
    private const string CalibrationCacheFile = @"C:\ProgramData\OmenFlow\fan_calibration.json";

    // Current active calibration (starts from family defaults, may be narrowed by field data)
    private FanCalibrationProfile _profile;

    public FanCalibrationService(BoardConfiguration boardConfig)
    {
        _boardConfig = boardConfig;
        _profile = BuildFamilyDefaults(boardConfig);

        // Try to load per-device calibration override
        LoadCalibrationOverride();
    }

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>Current active calibration profile.</summary>
    public FanCalibrationProfile Profile => _profile;

    /// <summary>
    /// Converts a fan level percent (0-100) to an estimated RPM range for the CPU fan.
    /// Returns (minRpm, maxRpm) with ±tolerance applied.
    /// </summary>
    public (int minRpm, int maxRpm) EstimateCpuRpm(int percent)
        => EstimateRpm(percent, _profile.CpuMaxRpm, _profile.CpuMinIdleRpm, _profile.RpmTolerance);

    /// <summary>
    /// Converts a fan level percent (0-100) to an estimated RPM range for the GPU fan.
    /// Returns (minRpm, maxRpm) with ±tolerance applied.
    /// </summary>
    public (int minRpm, int maxRpm) EstimateGpuRpm(int percent)
        => EstimateRpm(percent, _profile.GpuMaxRpm, _profile.GpuMinIdleRpm, _profile.RpmTolerance);

    /// <summary>
    /// Converts an observed RPM to an approximate fan percent for display purposes.
    /// </summary>
    public int CpuRpmToPercent(int rpm)
        => RpmToPercent(rpm, _profile.CpuMaxRpm);

    public int GpuRpmToPercent(int rpm)
        => RpmToPercent(rpm, _profile.GpuMaxRpm);

    /// <summary>
    /// Records a new calibration data point (actual RPM at a known percent).
    /// Narrows the RPM range over time. Persists to disk.
    /// </summary>
    public void RecordCalibrationPoint(int percent, int cpuRpm, int gpuRpm)
    {
        if (percent <= 0 || cpuRpm <= 0) return;

        // Update observed max if this is near full speed
        if (percent >= 90 && cpuRpm > _profile.CpuObservedMaxRpm)
        {
            _profile = _profile with { CpuObservedMaxRpm = cpuRpm };
            Console.WriteLine($"[FanCalib] New CPU max RPM observed: {cpuRpm} at {percent}%");
        }
        if (percent >= 90 && gpuRpm > _profile.GpuObservedMaxRpm && gpuRpm > 0)
        {
            _profile = _profile with { GpuObservedMaxRpm = gpuRpm };
            Console.WriteLine($"[FanCalib] New GPU max RPM observed: {gpuRpm} at {percent}%");
        }

        // Record the sample
        _profile.DataPoints.Add(new CalibrationDataPoint(
            DateTime.UtcNow, percent, cpuRpm, gpuRpm, _boardConfig.BoardId));

        // Keep only the last 200 data points
        if (_profile.DataPoints.Count > 200)
            _profile.DataPoints.RemoveAt(0);

        // Persist asynchronously
        SaveCalibrationOverrideAsync();
    }

    /// <summary>
    /// Returns a formatted summary for diagnostics export.
    /// </summary>
    public string GetCalibrationReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== OmenFlow Fan Calibration Report ===");
        sb.AppendLine($"BoardId       : {_boardConfig.BoardId}");
        sb.AppendLine($"Family        : {_boardConfig.Family}");
        sb.AppendLine($"MaxFanLevel   : {_boardConfig.MaxFanLevel}");
        sb.AppendLine($"FanCount      : {_boardConfig.FanCount}");
        sb.AppendLine($"CPU MaxRPM    : {_profile.CpuMaxRpm} (observed: {_profile.CpuObservedMaxRpm})");
        sb.AppendLine($"GPU MaxRPM    : {_profile.GpuMaxRpm} (observed: {_profile.GpuObservedMaxRpm})");
        sb.AppendLine($"Tolerance     : ±{_profile.RpmTolerance * 100:F0}%");
        sb.AppendLine($"DataPoints    : {_profile.DataPoints.Count}");

        if (_profile.DataPoints.Count > 0)
        {
            sb.AppendLine("\nRecent calibration points:");
            foreach (var p in _profile.DataPoints.TakeLast(10))
            {
                sb.AppendLine($"  {p.TimestampUtc:HH:mm:ss} | {p.FanPercent,3}% → CPU={p.CpuRpm}RPM, GPU={p.GpuRpm}RPM");
            }
        }
        return sb.ToString();
    }

    // ── Internals ──────────────────────────────────────────────────────────

    private static (int min, int max) EstimateRpm(int percent, int maxRpm, int minIdleRpm, double tolerance)
    {
        if (percent <= 0) return (0, 0);
        int expected = Math.Max(minIdleRpm, (int)(maxRpm * percent / 100.0));
        int low  = (int)(expected * (1.0 - tolerance));
        int high = (int)(expected * (1.0 + tolerance));
        return (Math.Max(0, low), high);
    }

    private static int RpmToPercent(int rpm, int maxRpm)
    {
        if (rpm <= 0 || maxRpm <= 0) return 0;
        return Math.Clamp((int)(rpm * 100.0 / maxRpm), 0, 100);
    }

    private static FanCalibrationProfile BuildFamilyDefaults(BoardConfiguration config)
    {
        // Family-specific RPM ranges from OmenCore field data + HP service manuals
        return config.Family switch
        {
            DeviceFamily.OmenV2 => new FanCalibrationProfile
            {
                CpuMaxRpm    = 6500,
                CpuMinIdleRpm = 1200,
                GpuMaxRpm    = 6800,
                GpuMinIdleRpm = 1200,
                RpmTolerance = 0.30
            },
            DeviceFamily.OmenV1 => new FanCalibrationProfile
            {
                CpuMaxRpm    = 5800,
                CpuMinIdleRpm = 1000,
                GpuMaxRpm    = 6200,
                GpuMinIdleRpm = 1000,
                RpmTolerance = 0.35
            },
            DeviceFamily.VictusS => new FanCalibrationProfile
            {
                CpuMaxRpm    = 5500,
                CpuMinIdleRpm = 900,
                GpuMaxRpm    = 5800,
                GpuMinIdleRpm = 900,
                RpmTolerance = 0.35
            },
            DeviceFamily.Victus => new FanCalibrationProfile
            {
                CpuMaxRpm    = 5200,
                CpuMinIdleRpm = 800,
                GpuMaxRpm    = 5500,
                GpuMinIdleRpm = 800,
                RpmTolerance = 0.40
            },
            DeviceFamily.OmenLegacy => new FanCalibrationProfile
            {
                CpuMaxRpm    = 5500,
                CpuMinIdleRpm = 1000,
                GpuMaxRpm    = 6000,
                GpuMinIdleRpm = 1000,
                RpmTolerance = 0.40
            },
            _ => new FanCalibrationProfile
            {
                CpuMaxRpm    = 6000,
                CpuMinIdleRpm = 1000,
                GpuMaxRpm    = 6200,
                GpuMinIdleRpm = 1000,
                RpmTolerance = 0.40
            }
        };
    }

    private void LoadCalibrationOverride()
    {
        try
        {
            if (!System.IO.File.Exists(CalibrationCacheFile)) return;
            var json = System.IO.File.ReadAllText(CalibrationCacheFile);
            var saved = JsonSerializer.Deserialize<SavedCalibration>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (saved != null && saved.BoardId == _boardConfig.BoardId)
            {
                if (saved.CpuObservedMaxRpm > 0)
                    _profile = _profile with { CpuObservedMaxRpm = saved.CpuObservedMaxRpm };
                if (saved.GpuObservedMaxRpm > 0)
                    _profile = _profile with { GpuObservedMaxRpm = saved.GpuObservedMaxRpm };
                if (saved.DataPoints?.Count > 0)
                    _profile.DataPoints.AddRange(saved.DataPoints);

                Console.WriteLine($"[FanCalib] Loaded calibration override for {_boardConfig.BoardId}: " +
                                  $"CPU max={_profile.CpuObservedMaxRpm}, GPU max={_profile.GpuObservedMaxRpm}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FanCalib] Failed to load calibration override: {ex.Message}");
        }
    }

    private async void SaveCalibrationOverrideAsync()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(CalibrationCacheFile);
            if (dir != null) System.IO.Directory.CreateDirectory(dir);

            var saved = new SavedCalibration
            {
                BoardId = _boardConfig.BoardId,
                CpuObservedMaxRpm = _profile.CpuObservedMaxRpm,
                GpuObservedMaxRpm = _profile.GpuObservedMaxRpm,
                DataPoints = _profile.DataPoints.TakeLast(50).ToList()
            };

            var json = JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(CalibrationCacheFile, json);
        }
        catch { }
    }

    // DTO for JSON persistence
    private sealed class SavedCalibration
    {
        public string BoardId { get; set; } = "";
        public int CpuObservedMaxRpm { get; set; }
        public int GpuObservedMaxRpm { get; set; }
        public List<CalibrationDataPoint> DataPoints { get; set; } = new();
    }
}

// ── Data models ────────────────────────────────────────────────────────────

public record FanCalibrationProfile
{
    public int CpuMaxRpm       { get; set; }
    public int CpuMinIdleRpm   { get; set; }
    public int GpuMaxRpm       { get; set; }
    public int GpuMinIdleRpm   { get; set; }
    public double RpmTolerance { get; set; } = 0.35;
    public int CpuObservedMaxRpm { get; set; }
    public int GpuObservedMaxRpm { get; set; }
    public List<CalibrationDataPoint> DataPoints { get; set; } = new();
}

public record CalibrationDataPoint(
    DateTime TimestampUtc,
    int FanPercent,
    int CpuRpm,
    int GpuRpm,
    string BoardId);
