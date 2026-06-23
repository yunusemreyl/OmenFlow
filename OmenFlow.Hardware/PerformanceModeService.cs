using System;
using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Interfaces;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

/// <summary>
/// Manages HP OMEN thermal/performance profiles via WMI BIOS.
///
/// Key behaviors adopted from OmenCore:
/// 1. Countdown Extension: After setting Performance/Quiet mode, a periodic timer re-sends the 0x1A
///    command every ~25 seconds to prevent BIOS from reverting to Default on its own.
/// 2. GPU power is coupled to the thermal profile (matching OmenCore's default behavior).
/// 3. CPU limits reset to profile defaults via 0x29.
/// 4. Retry logic (3 attempts) for WMI stability.
/// </summary>
public class PerformanceModeService : IPerformanceModeService, IDisposable
{
    private readonly IBiosService _biosService;
    private readonly IEcService _ecService;
    private readonly BoardConfiguration _boardConfig;
    private readonly GpuControlService _gpuControlService;

    private ThermalProfile _currentMode = ThermalProfile.Default;

    // Countdown Extension Timer (OmenCore approach)
    // BIOS may revert Performance/Cool modes after ~30s without reinforcement.
    // We re-send the thermal policy command every 30s to hold the setting.
    private Timer? _countdownExtTimer;
    private readonly object _timerLock = new();
    private const int CountdownExtIntervalMs = 30_000; // 30 seconds

    public PerformanceModeService(IBiosService biosService, IEcService ecService, BoardConfiguration boardConfig, GpuControlService gpuControlService)
    {
        _biosService = biosService;
        _ecService = ecService;
        _boardConfig = boardConfig;
        _gpuControlService = gpuControlService;

        _ = InitializeCurrentModeAsync();
    }

    private async Task InitializeCurrentModeAsync()
    {
        try
        {
            byte modeByte = await _ecService.ReadByteAsync(0x95);
            _currentMode = modeByte switch
            {
                0x00 => ThermalProfile.Default,
                0x01 => ThermalProfile.Performance,
                0x02 => ThermalProfile.Quiet,
                _ => ThermalProfile.Default
            };
            Console.WriteLine($"[PerformanceMode] Initial mode read from EC 0x95: 0x{modeByte:X2} -> {_currentMode}");
            
            if (_currentMode != ThermalProfile.Default)
            {
                StartCountdownExtension(_currentMode);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PerformanceMode] Failed to read initial mode from EC: {ex.Message}");
        }
    }

    public Task<ThermalProfile> GetCurrentModeAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_currentMode);
    }

    public async Task<bool> SetPerformanceModeAsync(ThermalProfile mode, CancellationToken ct = default)
    {
        Console.WriteLine($"[PerformanceMode] Setting mode: {mode} (0x{(byte)mode:X2})");

        bool success = false;

        // Step 1: WMI Thermal Policy (0x1A) — with retry
        byte wmiByte = (byte)mode;
        for (int i = 0; i < 3; i++)
        {
            var (ret, _) = await _biosService.SendCommandAsync(0x20008, 0x1A, new byte[] { 0xFF, wmiByte, 0x00, 0x00 }, 0, ct);
            if (ret == 0)
            {
                success = true;
                break;
            }
            Console.WriteLine($"[PerformanceMode] WMI 0x1A attempt {i + 1} failed (ret={ret}), retrying...");
            await Task.Delay(150, ct);
        }

        if (!success)
        {
            Console.WriteLine("[PerformanceMode] ✗ WMI thermal policy command failed after retries.");
            return false;
        }

        _currentMode = mode;
        Console.WriteLine($"[PerformanceMode] ✓ WMI 0x1A applied: {mode}");

        // GPU power and CPU limits coupling removed to match OmenCore behavior.
        // Users should control GPU power independently.

        // Step 4: EC mode byte fallback (belt-and-suspenders)
        if (_boardConfig.UseSimplifiedPerformanceMode)
        {
            byte ecValue = mode switch
            {
                ThermalProfile.Quiet       => 0x00,
                ThermalProfile.Default     => 0x01,
                ThermalProfile.Performance => 0x02,
                _                          => 0x01
            };
            try
            {
                await _ecService.WriteByteAsync(0xCE, ecValue, ct);
                Console.WriteLine($"[PerformanceMode] EC 0xCE ← 0x{ecValue:X2} ✓");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PerformanceMode] EC 0xCE write failed: {ex.Message}");
            }
        }

        // Step 5: Countdown Extension
        // Default mode: stop the timer (BIOS auto-manages)
        // Performance/Quiet: start timer to prevent BIOS from reverting the policy
        if (mode == ThermalProfile.Default)
        {
            StopCountdownExtension();
            Console.WriteLine("[PerformanceMode] Countdown extension stopped (Default mode).");
        }
        else
        {
            StartCountdownExtension(mode);
            Console.WriteLine($"[PerformanceMode] Countdown extension started for {mode} mode.");
        }

        return true;
    }

    /// <summary>
    /// Starts a periodic timer that re-sends the active thermal profile WMI command.
    /// This prevents BIOS from reverting Performance/Quiet modes after its internal countdown expires.
    /// Mirrors OmenCore's StartCountdownExtension() in WmiFanController.
    /// </summary>
    private void StartCountdownExtension(ThermalProfile mode)
    {
        lock (_timerLock)
        {
            // Stop existing before creating new
            _countdownExtTimer?.Dispose();
            _countdownExtTimer = new Timer(
                async _ => await CountdownExtensionTickAsync(mode),
                null,
                CountdownExtIntervalMs,
                CountdownExtIntervalMs);
        }
    }

    private void StopCountdownExtension()
    {
        lock (_timerLock)
        {
            _countdownExtTimer?.Dispose();
            _countdownExtTimer = null;
        }
    }

    private async Task CountdownExtensionTickAsync(ThermalProfile mode)
    {
        try
        {
            byte wmiByte = (byte)mode;
            var (ret, _) = await _biosService.SendCommandAsync(
                0x20008, 0x1A,
                new byte[] { 0xFF, wmiByte, 0x00, 0x00 },
                0);

            if (ret == 0)
                Console.WriteLine($"[PerformanceMode] Countdown extension tick: {mode} ✓");
            else
                Console.WriteLine($"[PerformanceMode] Countdown extension tick failed: ret={ret}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PerformanceMode] Countdown extension tick error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        StopCountdownExtension();
    }
}
