using System.IO;
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
    
    private ThermalProfile _cachedProfile = ThermalProfile.Default;
    private const string CacheFilePath = @"C:\ProgramData\OmenFlow\profile_cache.txt";

    private Timer? _countdownTimer;
    private int _lastManualPercent = -1;
    private bool _isManualControlActive = false;
    private bool _isMaxFanPresetActive = false;
    private readonly object _timerLock = new();

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
            return true;
        }
        
        // 2. Try WMI SetFanLevel
        if (enabled)
        {
            return await SetFanLevelAsync(100, cancellationToken);
        }
        else
        {
            return await RestoreAutoControlAsync(cancellationToken);
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
            return true;
        }

        // 3. EC Fallback
        try
        {
            byte ecMapped = MapPercentToFanLevel(percent, _boardConfig.MaxFanLevel);
            await _ecService.WriteByteAsync(0x34, ecMapped, cancellationToken);
            await _ecService.WriteByteAsync(0x35, ecMapped, cancellationToken);
            return true;
        }
        catch { }

        return false;
    }

    public async Task<bool> RestoreAutoControlAsync(CancellationToken cancellationToken = default)
    {
        StopCountdownTimer();
        _isManualControlActive = false;
        _isMaxFanPresetActive = false;

        bool success = false;

        Console.WriteLine("[FanControlService] Starting comprehensive EC Reset to Defaults (OmenCore sequence)...");

        try
        {
            // Step 1: Disable Max Fan mode (0x27) first so BIOS can accept thermal policy changes
            await _biosService.SendCommandAsync(0x20008, 0x27, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 0, cancellationToken);
            await Task.Delay(50, cancellationToken);

            // Step 2: Set Thermal Policy to Default (0x1A, 0x30)
            var (ret1A, _) = await _biosService.SendCommandAsync(
                0x20008, 0x1A,
                new byte[] { 0xFF, (byte)ThermalProfile.Default, 0x00, 0x00 },
                0, cancellationToken);

            if (ret1A == 0)
            {
                success = true;
                UpdateThermalProfileCache(ThermalProfile.Default);
                Console.WriteLine("  Step 2: SetFanMode(Default) succeeded");
            }
            await Task.Delay(50, cancellationToken);

            // Step 3: Extend countdown to prevent immediate timeout (CMD_FAN_GET_COUNT 0x10)
            await _biosService.SendCommandAsync(0x20008, 0x10, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 4, cancellationToken);
            await Task.Delay(50, cancellationToken);

            // Step 4: Reset EC Fan Profile Register (0x95) to Default (0x00) to clear any manual/override state in EC
            try
            {
                await _ecService.WriteByteAsync(0x95, 0x00, cancellationToken);
                Console.WriteLine("  Step 4: EC 0x95 ← 0x00 (Balanced/Default) succeeded");
            }
            catch { }
            await Task.Delay(50, cancellationToken);

            // Step 5: Final reinforcement of Thermal Policy Default (0x1A, 0x30)
            await _biosService.SendCommandAsync(
                0x20008, 0x1A,
                new byte[] { 0xFF, (byte)ThermalProfile.Default, 0x00, 0x00 },
                0, cancellationToken);
            await Task.Delay(50, cancellationToken);

            // Step 6: V1/V2 difference: V1 requires transition hint (20,20) and floor clear (0,0). 
            // V2 systems (MaxFanLevel >= 100) use percentage scale where SetFanLevel(0,0) means 
            // "0% duty cycle", which freezes the fans at 0 RPM. On V2, we skip SetFanLevel entirely!
            if (_boardConfig.MaxFanLevel < 100)
            {
                Console.WriteLine("  Step 6: Sending V1 fan transition hint (20, 20) and floor clear (0, 0)...");
                await _biosService.SendCommandAsync(0x20008, 0x2E, new byte[] { 20, 20, 0x00, 0x00 }, 0, cancellationToken);
                await Task.Delay(50, cancellationToken);
                await _biosService.SendCommandAsync(0x20008, 0x2E, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 0, cancellationToken);
            }
            else
            {
                Console.WriteLine("  Step 6: Skipping SetFanLevel(0,0) on V2 system to prevent 0 RPM fan freeze. BIOS has full auto control.");
            }

            Console.WriteLine("[FanControlService] ✓ EC Reset to Defaults completed successfully. BIOS should now have full control of fans.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WMI Auto Restore] failed: {ex.Message}");
        }

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
}
