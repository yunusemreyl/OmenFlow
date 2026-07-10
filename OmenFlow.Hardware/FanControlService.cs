using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Interfaces;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

public class FanControlService : IFanControlService, IDisposable
{
    private readonly IBiosService _biosService;
    private readonly IEcService _ecService;
    private readonly BoardConfiguration _boardConfig;
    
    private readonly Queue<FanCommandEntry> _commandHistory = new();
    private readonly object _historyLock = new();
    private const int MaxCommandHistory = 80;
    
    private ThermalProfile _cachedProfile = ThermalProfile.Default;
    private const string CacheFilePath = @"C:\ProgramData\OmenFlow\profile_cache.txt";

    private Timer? _countdownTimer;
    private int _lastManualPercent = -1;
    private bool _isManualControlActive = false;
    private bool _isMaxFanPresetActive = false;
    private readonly object _timerLock = new();

    // Fan transition window: when a preset/mode changes, BIOS briefly resets WMI registers,
    // causing RPM reads to return 0. Hold transition state for 5s so UI shows last known RPM.
    private volatile bool _isFanTransitioning = false;
    private DateTime _fanTransitionUntil = DateTime.MinValue;
    private const int FanTransitionHoldMs = 5000;
    public bool IsFanTransitioning => _isFanTransitioning && DateTime.UtcNow < _fanTransitionUntil;

    public void NotifyFanTransitionStarted()
    {
        _isFanTransitioning = true;
        _fanTransitionUntil = DateTime.UtcNow.AddMilliseconds(FanTransitionHoldMs);
        Console.WriteLine("[FanControlService] Fan transition window started (5s RPM hold).");
    }

    public FanControlService(IBiosService biosService, BoardConfiguration boardConfig, IEcService ecService)
    {
        _biosService = biosService;
        _boardConfig = boardConfig;
        _ecService = ecService;

        try
        {
            if (File.Exists(CacheFilePath))
            {
                var content = File.ReadAllText(CacheFilePath);
                if (Enum.TryParse<ThermalProfile>(content, out var p))
                {
                    _cachedProfile = p;
                }
            }
        }
        catch { }

        // Run Wake-Up sequence for 2023+ models to ensure WMI is unlocked (Fire and forget to avoid blocking startup)
        _ = WakeUpWmiAsync();
    }

    private async Task WakeUpWmiAsync()
    {
        Console.WriteLine("[FanControlService] Sending Wake-Up sequence to WMI...");
        for (int i = 0; i < 3; i++)
        {
            try
            {
                // CMD_FAN_GET_COUNT (0x10) with 4-byte payload wakes up the interface
                await _biosService.SendCommandAsync(0x20008, 0x10, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 4);
                
                // Also query system data (0x28) as OmenCore does in QuerySystemData()
                await _biosService.SendCommandAsync(0x20008, 0x28, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 128);
            }
            catch { }
            await Task.Delay(200);
        }
    }

    public async Task<(int CpuFanRpm, int GpuFanRpm)> GetFanRpmAsync(CancellationToken ct = default)
    {
        // 1. Try WMI 0x38 (CMD_FAN_GET_RPM for newer V2 systems) - Actual hardware tachometer RPM
        try
        {
            var (ret, outData) = await _biosService.SendCommandAsync(0x20008, 0x38, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 128, ct);
            if (ret == 0 && outData.Length >= 4)
            {
                int cpuRpm = outData[0] | (outData[1] << 8);
                int gpuRpm = outData[2] | (outData[3] << 8);
                
                if ((cpuRpm > 0 && cpuRpm <= 10000) || (gpuRpm > 0 && gpuRpm <= 10000))
                {
                    return (cpuRpm, gpuRpm);
                }
            }
        }
        catch { }

        // 2. Try EC direct tachometer read (Registers 0xD0 - 0xD3) - Actual hardware tachometer RPM
        try
        {
            byte cLow = await _ecService.ReadByteAsync(0xD0, ct);
            byte cHigh = await _ecService.ReadByteAsync(0xD1, ct);
            byte gLow = await _ecService.ReadByteAsync(0xD2, ct);
            byte gHigh = await _ecService.ReadByteAsync(0xD3, ct);

            int cpuRpm = (cHigh << 8) | cLow;
            int gpuRpm = (gHigh << 8) | gLow;

            if ((cpuRpm > 0 && cpuRpm < 10000) || (gpuRpm > 0 && gpuRpm < 10000))
            {
                return (cpuRpm, gpuRpm);
            }
        }
        catch { }

        // 3. Try WMI 0x2D (CMD_FAN_GET_LEVEL). On Victus systems (like 8BBE), this returns the active LUT level (0-55).
        try
        {
            var (ret, outData) = await _biosService.SendCommandAsync(0x20008, 0x2D, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 128, ct);
            if (ret == 0 && outData.Length >= 2 && (outData[0] > 0 || outData[1] > 0))
            {
                int maxLevel = Math.Clamp(_boardConfig.MaxFanLevel, 1, 100);
                int cpuRpm = Math.Clamp((outData[0] * 5800) / maxLevel, 0, 5800);
                int gpuRpm = Math.Clamp((outData[1] * 6100) / maxLevel, 0, 6100);
                
                return (cpuRpm, gpuRpm);
            }
        }
        catch { }

        // 4. Try WMI 0x37 (CMD_FAN_GET_LEVEL_V2 for OMEN Max 2025+ / V2 systems)
        try
        {
            var (ret, outData) = await _biosService.SendCommandAsync(0x20008, 0x37, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 128, ct);
            if (ret == 0 && outData.Length >= 2 && (outData[0] > 0 || outData[1] > 0))
            {
                int maxLevel = Math.Clamp(_boardConfig.MaxFanLevel, 1, 100);
                int cpuRpm = Math.Clamp((outData[0] * 5800) / maxLevel, 0, 5800);
                int gpuRpm = Math.Clamp((outData[1] * 6100) / maxLevel, 0, 6100);
                
                return (cpuRpm, gpuRpm);
            }
        }
        catch { }

        // If no valid reading, return 0 (do not estimate fake values)
        return (0, 0);
    }

    public async Task<int> GetCpuTemperatureAsync(CancellationToken ct = default)
    {
        var (ret, outData) = await _biosService.SendCommandAsync(0x20008, 0x23, new byte[] { 0x01, 0x00, 0x00, 0x00 }, 4, ct);
        if (ret != 0 || outData.Length < 1)
        {
            return 0; // sentinel
        }

        return outData[0];
    }

    public Task<ThermalProfile> GetThermalProfileAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_cachedProfile);
    }

    private static byte MapPercentToFanLevel(int percent, int maxFanLevel)
    {
        percent = Math.Clamp(percent, 0, 100);
        return (byte)(percent * Math.Clamp(maxFanLevel, 1, 100) / 100);
    }

    /// <summary>
    /// Updates the local thermal profile cache (used for telemetry reads).
    /// Actual hardware application is handled by <see cref="PerformanceModeService"/>.
    /// Called internally by RestoreAutoControlAsync when reverting to Default.
    /// </summary>
    internal void UpdateThermalProfileCache(ThermalProfile profile)
    {
        _cachedProfile = profile;
        try
        {
            var dir = Path.GetDirectoryName(CacheFilePath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(CacheFilePath, profile.ToString());
        }
        catch { }
    }


    public async Task<bool> SetMaxFanAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _isMaxFanPresetActive = enabled;
        if (enabled) 
        {
            _lastManualPercent = 100;
            // Set Performance thermal policy first so BIOS unlocks max TDP
            await _biosService.SendCommandAsync(
                0x20008, 0x1A,
                new byte[] { 0xFF, (byte)ThermalProfile.Performance, 0x00, 0x00 },
                0, cancellationToken);
        }
        
        // 1. Try WMI Max Fan (0x27)
        bool wmiSuccess = false;
        try
        {
            var (ret, _) = await _biosService.SendCommandAsync(0x20008, 0x27, new byte[] { (byte)(enabled ? 0x01 : 0x00), 0x00, 0x00, 0x00 }, 0, cancellationToken);
            if (ret == 0) wmiSuccess = true;
        }
        catch { }

        if (wmiSuccess)
        {
            if (enabled)
            {
                _isManualControlActive = true;
                StartCountdownTimer();
            }
            else
            {
                StopCountdownTimer();
                _isManualControlActive = false;
                // When disabling Max Fan, we must run the full RestoreAutoControlAsync sequence so BIOS takes back control!
                await RestoreAutoControlAsync(cancellationToken);
            }
            RecordCommand("SetMaxFan", enabled.ToString(), true, "WMI Max Fan command succeeded.");
            return true;
        }
        
        // 2. Try WMI SetFanLevel
        if (enabled)
        {
            bool fallbackSuccess = await SetFanLevelAsync(100, cancellationToken);
            RecordCommand("SetMaxFan", enabled.ToString(), fallbackSuccess, fallbackSuccess ? "Fallback to SetFanLevel(100) succeeded." : "Fallback to SetFanLevel(100) failed.");
            return fallbackSuccess;
        }
        else
        {
            bool fallbackSuccess = await RestoreAutoControlAsync(cancellationToken);
            RecordCommand("SetMaxFan", enabled.ToString(), fallbackSuccess, fallbackSuccess ? "Fallback to RestoreAutoControl succeeded." : "Fallback to RestoreAutoControl failed.");
            return fallbackSuccess;
        }
    }

    public async Task<bool> SetFanLevelAsync(int percent, CancellationToken cancellationToken = default)
    {
        percent = Math.Clamp(percent, 0, 100);

        // Manual 0% can wedge some firmware states (fans stop and fail to recover).
        // For safety and model compatibility (matching OmenCore), map 0% to BIOS auto mode.
        if (percent == 0)
        {
            Console.WriteLine("[FanControlService] SetFanLevelAsync(0) mapped to RestoreAutoControlAsync for firmware-safe silent behavior.");
            return await RestoreAutoControlAsync(cancellationToken);
        }

        _lastManualPercent = percent;
        _isMaxFanPresetActive = false;

        // 1. Ensure Max Fan mode (0x27) is disabled before applying custom fan level (0x2E) to prevent conflicts
        await _biosService.SendCommandAsync(0x20008, 0x27, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 0, cancellationToken);

        // 2. WMI Attempt with the clean mapped value
        byte bMapped = MapPercentToFanLevel(percent, _boardConfig.MaxFanLevel);
        
        bool wmiSuccess = false;
        int maxRetries = 3;
        for (int i = 0; i < maxRetries && !wmiSuccess; i++)
        {
            try
            {
                var (ret, _) = await _biosService.SendCommandAsync(0x20008, 0x2E, new byte[] { bMapped, bMapped, 0x00, 0x00 }, 0, cancellationToken);
                if (ret == 0) wmiSuccess = true;
            }
            catch { }
            if (!wmiSuccess) await Task.Delay(100, cancellationToken);
        }

        if (wmiSuccess)
        {
            _isManualControlActive = true;
            StartCountdownTimer();
            RecordCommand("SetFanLevel", $"{percent}%", true, $"WMI 0x2E set fan level (mapped={bMapped}) succeeded.");
            return true;
        }

        // 3. EC Fallback
        try
        {
            byte ecMapped = MapPercentToFanLevel(percent, _boardConfig.MaxFanLevel);
            await _ecService.WriteByteAsync(0x34, ecMapped, cancellationToken);
            await _ecService.WriteByteAsync(0x35, ecMapped, cancellationToken);
            RecordCommand("SetFanLevel", $"{percent}%", true, $"EC 0x34/0x35 write (mapped={ecMapped}) succeeded.");
            return true;
        }
        catch (Exception ex)
        {
            RecordCommand("SetFanLevel", $"{percent}%", false, $"EC write failed: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Sets CPU and GPU fans to independent speeds using the V2-capable WMI command.
    /// Falls back to unified SetFanLevelAsync on single-fan or WMI-only models.
    /// </summary>
    public async Task<bool> SetFanLevelIndependentAsync(int cpuPercent, int gpuPercent, CancellationToken cancellationToken = default)
    {
        cpuPercent = Math.Clamp(cpuPercent, 0, 100);
        gpuPercent = Math.Clamp(gpuPercent, 0, 100);

        // Single-fan models or models without EC support: use unified level
        if (_boardConfig.FanCount <= 1 || !_boardConfig.SupportsFanControlEc)
        {
            int unified = Math.Max(cpuPercent, gpuPercent);
            Console.WriteLine($"[FanControlService] SetFanLevelIndependentAsync → unified fallback ({unified}%) [FanCount={_boardConfig.FanCount}, EC={_boardConfig.SupportsFanControlEc}]");
            return await SetFanLevelAsync(unified, cancellationToken);
        }

        // Disable Max Fan mode first to prevent conflicts
        await _biosService.SendCommandAsync(0x20008, 0x27, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 0, cancellationToken);

        byte bCpu = MapPercentToFanLevel(cpuPercent, _boardConfig.MaxFanLevel);
        byte bGpu = MapPercentToFanLevel(gpuPercent, _boardConfig.MaxFanLevel);

        bool wmiSuccess = false;
        for (int i = 0; i < 3 && !wmiSuccess; i++)
        {
            try
            {
                // CMD_FAN_SET_LEVEL (0x2E): byte[0]=CPU fan level, byte[1]=GPU fan level
                var (ret, _) = await _biosService.SendCommandAsync(0x20008, 0x2E, new byte[] { bCpu, bGpu, 0x00, 0x00 }, 0, cancellationToken);
                if (ret == 0) wmiSuccess = true;
            }
            catch { }
            if (!wmiSuccess) await Task.Delay(100, cancellationToken);
        }

        if (wmiSuccess)
        {
            _lastManualPercent = Math.Max(cpuPercent, gpuPercent);
            _isManualControlActive = true;
            StartCountdownTimer();
            Console.WriteLine($"[FanControlService] SetFanLevelIndependentAsync: CPU={cpuPercent}% (L={bCpu}), GPU={gpuPercent}% (L={bGpu}) ✓");
            RecordCommand("SetFanLevelIndep", $"CPU={cpuPercent}%, GPU={gpuPercent}%", true, $"WMI 0x2E set independent level (C={bCpu}, G={bGpu}) succeeded.");
            return true;
        }

        // EC fallback: write CPU and GPU registers separately
        try
        {
            await _ecService.WriteByteAsync(0x34, bCpu, cancellationToken); // CPU fan
            await _ecService.WriteByteAsync(0x35, bGpu, cancellationToken); // GPU fan
            _lastManualPercent = Math.Max(cpuPercent, gpuPercent);
            _isManualControlActive = true;
            Console.WriteLine($"[FanControlService] SetFanLevelIndependentAsync EC fallback: CPU=0x{bCpu:X2}, GPU=0x{bGpu:X2} ✓");
            RecordCommand("SetFanLevelIndep", $"CPU={cpuPercent}%, GPU={gpuPercent}%", true, $"EC 0x34/0x35 set independent level (C={bCpu}, G={bGpu}) succeeded.");
            return true;
        }
        catch (Exception ex)
        {
            RecordCommand("SetFanLevelIndep", $"CPU={cpuPercent}%, GPU={gpuPercent}%", false, $"EC independent write failed: {ex.Message}");
        }

        return false;
    }

    public async Task<bool> RestoreAutoControlAsync(CancellationToken cancellationToken = default)
    {
        StopCountdownTimer();
        _isManualControlActive = false;
        _isMaxFanPresetActive = false;

        ThermalProfile activeProfile = ThermalProfile.Default;
        try
        {
            byte modeByte = await _ecService.ReadByteAsync(0x95, cancellationToken);
            activeProfile = modeByte switch
            {
                0x01 => ThermalProfile.Performance,
                0x02 => ThermalProfile.Quiet,
                _ => ThermalProfile.Default
            };
        }
        catch { }

        bool success = false;
        Console.WriteLine($"[FanControlService] Starting EC Reset to Defaults (respecting profile {activeProfile})...");

        try
        {
            // Step 1: Disable Max Fan mode (0x27) first so BIOS can accept thermal policy changes
            await _biosService.SendCommandAsync(0x20008, 0x27, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 0, cancellationToken);
            await Task.Delay(50, cancellationToken);

            // Step 2: Set Thermal Policy to Default (30) as a safe intermediate state (OmenCore ritual)
            await _biosService.SendCommandAsync(
                0x20008, 0x1A,
                new byte[] { 0xFF, (byte)ThermalProfile.Default, 0x00, 0x00 },
                0, cancellationToken);
            await Task.Delay(50, cancellationToken);

            // Step 3: Apply physical fan kick (Level 20) to prevent firmware freeze (OmenCore ritual) - V1 systems ONLY
            if (_boardConfig.MaxFanLevel < 100)
            {
                await _biosService.SendCommandAsync(
                    0x20008, 0x2E,
                    new byte[] { 20, 20, 0x00, 0x00 },
                    0, cancellationToken);
                await Task.Delay(50, cancellationToken);
            }
            else
            {
                Console.WriteLine("  Step 3: Skipped SetFanLevel kick on V2 system to prevent overriding BIOS auto control");
            }

            // Step 4: Set Thermal Policy to the actual target profile
            var (ret1A, _) = await _biosService.SendCommandAsync(
                0x20008, 0x1A,
                new byte[] { 0xFF, (byte)activeProfile, 0x00, 0x00 },
                0, cancellationToken);

            if (ret1A == 0)
            {
                success = true;
                UpdateThermalProfileCache(activeProfile);
                Console.WriteLine($"  Step 4: SetFanMode({activeProfile}) succeeded");
            }
            await Task.Delay(50, cancellationToken);

            // Step 5: Extend countdown to prevent immediate timeout (CMD_FAN_GET_COUNT 0x10)
            await _biosService.SendCommandAsync(0x20008, 0x10, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 4, cancellationToken);
            await Task.Delay(50, cancellationToken);

            // Step 6: Reset EC Fan Profile Register to match active profile
            byte ecFanMode = activeProfile switch
            {
                ThermalProfile.Performance => 0x01,
                ThermalProfile.Quiet => 0x02,
                _ => 0x00
            };
            try
            {
                await _ecService.WriteByteAsync(0x95, ecFanMode, cancellationToken);
                Console.WriteLine($"  Step 6: EC 0x95 ← 0x{ecFanMode:X2} ({activeProfile}) succeeded");
            }
            catch { }
            
            Console.WriteLine("[FanControlService] ✓ EC Reset to Defaults (OmenCore Safe Sequence) completed successfully. BIOS should now have full control of fans.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WMI Auto Restore] failed: {ex.Message}");
            RecordCommand("RestoreAutoControl", activeProfile.ToString(), false, $"Exception: {ex.Message}");
        }

        RecordCommand("RestoreAutoControl", activeProfile.ToString(), success, success ? "BIOS auto control restored successfully." : "Failed to apply WMI profile.");
        return success;
    }


    private void StartCountdownTimer()
    {
        lock (_timerLock)
        {
            if (_countdownTimer == null)
            {
                // Fire every 5 seconds to keep manual mode alive
                _countdownTimer = new Timer(OnCountdownTick, null, 5000, 5000);
            }
        }
    }

    private void StopCountdownTimer()
    {
        lock (_timerLock)
        {
            _countdownTimer?.Dispose();
            _countdownTimer = null;
        }
    }

    private async void OnCountdownTick(object? state)
    {
        if (!_isManualControlActive) return;

        try
        {
            // Send CMD_FAN_GET_COUNT (0x10) to keep WMI interface awake / extend countdown
            await _biosService.SendCommandAsync(0x20008, 0x10, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 4);

            if (_isMaxFanPresetActive)
            {
                await _biosService.SendCommandAsync(0x20008, 0x27, new byte[] { 0x01, 0x00, 0x00, 0x00 }, 0);
            }
            else if (_lastManualPercent >= 0)
            {
                byte bMapped = MapPercentToFanLevel(_lastManualPercent, _boardConfig.MaxFanLevel);
                await _biosService.SendCommandAsync(0x20008, 0x2E, new byte[] { bMapped, bMapped, 0x00, 0x00 }, 0);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CountdownTick] Error maintaining WMI fan heartbeat: {ex.Message}");
        }
    }

    public void Dispose()
    {
        StopCountdownTimer();
    }

    public void RecordCommand(string command, string target, bool success, string details = "")
    {
        var entry = new FanCommandEntry(
            TimestampUtc: DateTime.UtcNow,
            Command: command,
            Target: target,
            Success: success,
            Backend: "WMI/EC",
            FanMode: _isMaxFanPresetActive ? 2 : _isManualControlActive ? 3 : 0,
            CurveActive: details.Contains("Curve") || details.Contains("curve"),
            ThermalProtectionActive: details.Contains("emergency") || details.Contains("Emergency"),
            CpuTempC: 0,
            GpuTempC: 0,
            CpuFanRpm: 0,
            GpuFanRpm: 0,
            Details: details
        );

        lock (_historyLock)
        {
            if (_commandHistory.Count >= MaxCommandHistory) _commandHistory.Dequeue();
            _commandHistory.Enqueue(entry);
        }
    }

    public string GetCommandHistoryReport()
    {
        List<FanCommandEntry> entries;
        lock (_historyLock)
        {
            entries = _commandHistory.ToList();
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== OmenFlow Fan Command History ===");
        sb.AppendLine($"Entries: {entries.Count} (max {MaxCommandHistory})");
        sb.AppendLine(new string('-', 80));
        foreach (var e in entries)
        {
            sb.AppendLine($"{e.TimestampUtc:O} | {(e.Success ? "OK" : "FAIL"),-4} | {e.Command,-20} | {e.Target}");
            sb.AppendLine($"  {e.Details}");
        }
        return sb.ToString();
    }
}
