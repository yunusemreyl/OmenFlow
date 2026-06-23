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
        // 1. Try to read real RPM from WMI 0x38 (V2 systems)
        try
        {
            var (ret, outData) = await _biosService.SendCommandAsync(0x20008, 0x38, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 128, ct);
            if (ret == 0 && outData.Length >= 4)
            {
                int cpuRpm = outData[0] | (outData[1] << 8);
                int gpuRpm = outData[2] | (outData[3] << 8);
                
                if (cpuRpm > 0 && cpuRpm <= 8000 || gpuRpm > 0 && gpuRpm <= 8000)
                {
                    return (cpuRpm, gpuRpm);
                }
            }
        }
        catch { }

        // 2. Fallback to WMI 0x2D (V1 systems). Returns fan level (0-100), not direct RPM.
        try
        {
            var (ret, outData) = await _biosService.SendCommandAsync(0x20008, 0x2D, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 128, ct);
            if (ret == 0 && outData.Length >= 2)
            {
                // Multiply level by max RPM (5500) and divide by 100 to estimate RPM
                int cpuRpm = Math.Clamp((outData[0] * 5500) / 100, 0, 8000);
                int gpuRpm = Math.Clamp((outData[1] * 5500) / 100, 0, 8000);
                
                if (cpuRpm > 0 || gpuRpm > 0)
                {
                    return (cpuRpm, gpuRpm);
                }
            }
        }
        catch { }

        // 3. Fallback to EC read if WMI didn't return data
        try
        {
            byte cLow = await _ecService.ReadByteAsync(0xD0, ct);
            byte cHigh = await _ecService.ReadByteAsync(0xD1, ct);
            byte gLow = await _ecService.ReadByteAsync(0xD2, ct);
            byte gHigh = await _ecService.ReadByteAsync(0xD3, ct);

            int cpuRpm = (cHigh << 8) | cLow;
            int gpuRpm = (gHigh << 8) | gLow;

            if (cpuRpm > 0 && cpuRpm < 10000 || gpuRpm > 0 && gpuRpm < 10000)
            {
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
        if (percent >= 100) return 100; // Ceiling for max fan
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
        _lastManualPercent = percent;

        // 1. WMI Attempt
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

        // 2. EC Fallback
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

        bool success = false;

        // Try WMI Auto Control first
        try
        {
            // Reset Thermal Policy to Default (0x1A)
            var (ret1A, _) = await _biosService.SendCommandAsync(
                0x20008, 0x1A,
                new byte[] { 0xFF, (byte)ThermalProfile.Default, 0x00, 0x00 },
                0, cancellationToken);

            if (ret1A == 0)
            {
                success = true;
                UpdateThermalProfileCache(ThermalProfile.Default);
            }

            // Disable Max Fan (0x27)
            await _biosService.SendCommandAsync(0x20008, 0x27, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 0, cancellationToken);

            // V1/V2 difference: V1 requires transition hint and floor clear. V2 uses only SetFanMode.
            if (_boardConfig.MaxFanLevel < 100)
            {
                // V1 transition hint
                await _biosService.SendCommandAsync(0x20008, 0x2E, new byte[] { 20, 20, 0x00, 0x00 }, 0, cancellationToken);
                await Task.Delay(50, cancellationToken);
                // Floor clear
                await _biosService.SendCommandAsync(0x20008, 0x2E, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 0, cancellationToken);
            }
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
        if (!_isManualControlActive || _lastManualPercent < 0) return;

        try
        {
            if (_lastManualPercent == 100)
            {
                await _biosService.SendCommandAsync(0x20008, 0x27, new byte[] { 0x01, 0x00, 0x00, 0x00 }, 0);
            }
            else
            {
                // Re-apply fan level with proper scaling to extend countdown
                byte bMapped = MapPercentToFanLevel(_lastManualPercent, _boardConfig.MaxFanLevel);
                await _biosService.SendCommandAsync(0x20008, 0x2E, new byte[] { bMapped, bMapped, 0x00, 0x00 }, 0);
            }

            // Also explicitly extend countdown by reading current value and writing it back if possible,
            // or by issuing a SetIdleMode(false) 0x19 equivalent, but SetFanLevel is usually enough.
            await ExtendFanCountdownAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CountdownTick] Error maintaining WMI fan heartbeat: {ex.Message}");
        }
    }

    private async Task ExtendFanCountdownAsync()
    {
        try
        {
            var (ret, outData) = await _biosService.SendCommandAsync(0x20008, 0x2D, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 128);
            if (ret == 0 && outData.Length >= 2)
            {
                await _biosService.SendCommandAsync(0x20008, 0x2E, new byte[] { outData[0], outData[1], 0x00, 0x00 }, 0);
            }
            else
            {
                await _biosService.SendCommandAsync(0x20008, 0x19, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 0);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        StopCountdownTimer();
    }
}
