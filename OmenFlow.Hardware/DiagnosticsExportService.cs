using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Interfaces;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

/// <summary>
/// Generates a comprehensive diagnostics ZIP archive for field support and bug reports.
///
/// Mirrors OmenCore's DiagnosticsExportService pattern:
/// - Collects EC register snapshot, fan command history, calibration report,
///   model capability summary, and power limit state
/// - Bundles everything into a ZIP with timestamp in the filename
/// - ZIP is saved to the user's Desktop for easy access
///
/// Contents of the ZIP:
///   diagnostics.txt           — Human-readable overview (model, mode, temp, RPM)
///   fan_command_history.txt   — Last 80 fan commands with timing and success
///   fan_calibration.txt       — Per-model RPM calibration data and data points
///   model_capabilities.txt    — BoardConfiguration dump
///   power_limits.txt          — Last known CPU PL1/PL2 and GPU TGP
///   ec_snapshot.txt           — Key EC register values (0x95, 0xCE, 0x34/35, 0xD0-D3)
///   event_log.txt             — Recent console log tail (if available)
/// </summary>
public class DiagnosticsExportService
{
    private readonly FanCurveHostedService _fanCurveService;
    private readonly FanCalibrationService _calibrationService;
    private readonly PowerLimitService? _powerLimitService;
    private readonly FanVerificationService? _verificationService;
    private readonly IEcService _ecService;
    private readonly BoardConfiguration _boardConfig;

    // Runtime telemetry snapshot injected by Worker
    public Func<WorkerTelemetry>? TelemetryProvider { get; set; }
    public Func<ThermalProfile>? CurrentProfileProvider { get; set; }
    public Func<int>? CurrentFanModeProvider { get; set; }

    // In-memory log buffer (Worker writes to this)
    private static readonly List<string> s_logBuffer = new();
    private static readonly object s_logLock = new();
    private const int MaxLogLines = 500;

    public DiagnosticsExportService(
        FanCurveHostedService fanCurveService,
        FanCalibrationService calibrationService,
        IEcService ecService,
        BoardConfiguration boardConfig,
        PowerLimitService? powerLimitService = null,
        FanVerificationService? verificationService = null)
    {
        _fanCurveService = fanCurveService;
        _calibrationService = calibrationService;
        _ecService = ecService;
        _boardConfig = boardConfig;
        _powerLimitService = powerLimitService;
        _verificationService = verificationService;
    }

    // ── Static log capture (call from Console.WriteLine replacement) ────────

    public static void AppendLog(string line)
    {
        lock (s_logLock)
        {
            s_logBuffer.Add($"{DateTime.Now:HH:mm:ss.fff} {line}");
            if (s_logBuffer.Count > MaxLogLines)
                s_logBuffer.RemoveAt(0);
        }
    }

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Generates the diagnostics ZIP and saves it to the Desktop.
    /// Returns the full path of the created ZIP file.
    /// </summary>
    public async Task<string> ExportAsync(CancellationToken ct = default)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string fileName = $"OmenFlow_Diagnostics_{timestamp}.zip";
        string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        string zipPath = Path.Combine(desktopPath, fileName);

        Console.WriteLine($"[Diagnostics] Starting export → {zipPath}");

        using var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false);

        // 1. Overview
        await AddEntryAsync(archive, "diagnostics.txt", await BuildOverviewAsync(ct), ct);

        // 2. Fan command history
        await AddEntryAsync(archive, "fan_command_history.txt", _fanCurveService.GetCommandHistoryReport(), ct);

        // 3. Fan calibration
        await AddEntryAsync(archive, "fan_calibration.txt", _calibrationService.GetCalibrationReport(), ct);

        // 4. Model capabilities
        await AddEntryAsync(archive, "model_capabilities.txt", BuildModelCapabilitiesReport(), ct);

        // 5. Power limits
        string powerReport = _powerLimitService?.GetDiagnosticsSummary()
                             ?? "PowerLimitService not initialized.";
        await AddEntryAsync(archive, "power_limits.txt", powerReport, ct);

        // 6. Fan verification state
        string verifyReport = _verificationService?.GetVerificationSummary()
                              ?? "FanVerificationService not initialized.";
        await AddEntryAsync(archive, "fan_verification.txt", verifyReport, ct);

        // 7. EC snapshot
        await AddEntryAsync(archive, "ec_snapshot.txt", await BuildEcSnapshotAsync(ct), ct);

        // 8. Event log
        string logReport;
        lock (s_logLock)
        {
            logReport = string.Join(Environment.NewLine, s_logBuffer);
        }
        await AddEntryAsync(archive, "event_log.txt", logReport, ct);

        Console.WriteLine($"[Diagnostics] ✓ Export complete: {zipPath}");
        return zipPath;
    }

    // ── Internal report builders ────────────────────────────────────────────

    private async Task<string> BuildOverviewAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== OmenFlow Diagnostics Overview ===");
        sb.AppendLine($"Generated  : {DateTime.Now:O}");
        sb.AppendLine($"Version    : OmenFlow (fan+perf diagnostics export)");
        sb.AppendLine();

        sb.AppendLine("── System ──");
        sb.AppendLine($"BoardId    : {_boardConfig.BoardId}");
        sb.AppendLine($"Family     : {_boardConfig.Family}");
        sb.AppendLine($"MaxFanLvl  : {_boardConfig.MaxFanLevel}");
        sb.AppendLine($"FanCount   : {_boardConfig.FanCount}");
        sb.AppendLine($"EC Offset  : {_boardConfig.HasEcThermalOffset}");
        sb.AppendLine($"Desktop    : {_boardConfig.IsDesktop}");
        sb.AppendLine();

        if (TelemetryProvider != null)
        {
            var tel = TelemetryProvider();
            sb.AppendLine("── Current Telemetry ──");
            sb.AppendLine($"CPU Temp   : {tel.CpuTemp}°C");
            sb.AppendLine($"GPU Temp   : {tel.GpuTemp}°C");
            sb.AppendLine($"CPU Fan    : {tel.CpuFanRpm} RPM");
            sb.AppendLine($"GPU Fan    : {tel.GpuFanRpm} RPM");
            sb.AppendLine($"CPU Load   : {tel.CpuLoad:F1}%");
            sb.AppendLine($"GPU Load   : {tel.GpuLoad:F1}%");
            sb.AppendLine($"CPU Power  : {tel.CpuPower:F1}W");
            sb.AppendLine($"GPU Power  : {tel.GpuPower:F1}W");
            sb.AppendLine();
        }

        if (CurrentProfileProvider != null)
            sb.AppendLine($"ThermalProfile  : {CurrentProfileProvider()}");
        if (CurrentFanModeProvider != null)
            sb.AppendLine($"ActiveFanMode   : {CurrentFanModeProvider()}");

        sb.AppendLine($"CurveActive     : {_fanCurveService.IsCurveOrHoldActive}");

        return sb.ToString();
    }

    private string BuildModelCapabilitiesReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Model Capabilities ===");
        sb.AppendLine($"BoardId                 : {_boardConfig.BoardId}");
        sb.AppendLine($"Family                  : {_boardConfig.Family}");
        sb.AppendLine($"MaxFanLevel             : {_boardConfig.MaxFanLevel}");
        sb.AppendLine($"FanCount                : {_boardConfig.FanCount}");
        sb.AppendLine($"SupportsFanCurves       : {_boardConfig.SupportsFanCurves}");
        sb.AppendLine($"SupportsFanControlEc    : {_boardConfig.SupportsFanControlEc}");
        sb.AppendLine($"SupportsDetailedPL      : {_boardConfig.SupportsDetailedPowerLimits}");
        sb.AppendLine($"UseSimplifiedPerfMode   : {_boardConfig.UseSimplifiedPerformanceMode}");
        sb.AppendLine($"HasEcThermalOffset      : {_boardConfig.HasEcThermalOffset}");
        sb.AppendLine($"IsDesktop               : {_boardConfig.IsDesktop}");
        return sb.ToString();
    }

    private async Task<string> BuildEcSnapshotAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== EC Register Snapshot ===");
        sb.AppendLine($"Timestamp: {DateTime.Now:O}");
        sb.AppendLine();

        var registers = new (string Name, byte Addr)[]
        {
            ("ThermalProfile (0x95)", 0x95),
            ("Fan Mode V2 (0xCE)", 0xCE),
            ("CPU Fan Level (0x34)", 0x34),
            ("GPU Fan Level (0x35)", 0x35),
            ("CPU Fan RPM Lo (0xD0)", 0xD0),
            ("CPU Fan RPM Hi (0xD1)", 0xD1),
            ("GPU Fan RPM Lo (0xD2)", 0xD2),
            ("GPU Fan RPM Hi (0xD3)", 0xD3),
            ("EC Thermal Offset (0x80)", 0x80),
            ("Power Limit State (0xA0)", 0xA0),
        };

        foreach (var (name, addr) in registers)
        {
            try
            {
                byte value = await _ecService.ReadByteAsync(addr, ct);
                sb.AppendLine($"  {name,-35}: 0x{value:X2} ({value})");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  {name,-35}: ERROR ({ex.Message})");
            }
        }

        return sb.ToString();
    }

    private static async Task AddEntryAsync(ZipArchive archive, string entryName, string content, CancellationToken ct)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        await writer.WriteAsync(content);
    }
}
