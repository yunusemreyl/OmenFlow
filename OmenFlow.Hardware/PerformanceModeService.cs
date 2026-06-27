using System;
using System.Runtime.InteropServices;
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
            Console.WriteLine("[PerformanceMode] ✗ WMI thermal policy command failed after retries. Proceeding to EC fallback.");
        }
        else
        {
            Console.WriteLine($"[PerformanceMode] ✓ WMI 0x1A applied: {mode}");
        }

        _currentMode = mode;

        // Apply Windows Power Plan & Boost Index (OmenCore behavior)
        Guid planGuid = mode switch
        {
            ThermalProfile.Performance => HighPerformancePlan,
            ThermalProfile.Quiet => PowerSaverPlan,
            _ => BalancedPlan
        };
        uint boostIndex = mode switch
        {
            ThermalProfile.Performance => 4, // Aggressive
            ThermalProfile.Quiet => 0, // Disabled / Efficient
            _ => 2 // Standard / Enabled
        };

        try
        {
            uint res = PowerSetActiveScheme(IntPtr.Zero, ref planGuid);
            Console.WriteLine($"[PerformanceMode] Windows Power Plan {planGuid} activation result: {res}");

            var processorGroup = ProcessorSubGroup;
            var boostSetting = ProcessorBoost;
            PowerWriteACValueIndex(IntPtr.Zero, IntPtr.Zero, ref processorGroup, ref boostSetting, boostIndex);
            PowerWriteDCValueIndex(IntPtr.Zero, IntPtr.Zero, ref processorGroup, ref boostSetting, boostIndex);
            Console.WriteLine($"[PerformanceMode] Processor boost index set to {boostIndex}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PerformanceMode] Failed to set Windows Power Plan: {ex.Message}");
        }

        // Apply GPU Dynamic Boost Coupling (OmenCore behavior)
        GpuPowerLevel gpuPower = mode switch
        {
            ThermalProfile.Performance => GpuPowerLevel.MaxPower,
            ThermalProfile.Quiet => GpuPowerLevel.BasePower,
            _ => GpuPowerLevel.ExtraPower
        };
        await _gpuControlService.SetGpuPowerAsync(gpuPower, ct);
        Console.WriteLine($"[PerformanceMode] GPU Dynamic Boost coupled to: {gpuPower}");

        // Step 4: EC mode byte fallback & Fan Profile Kick Down (0x95 & 0xCE)
        byte ecFanMode = mode switch
        {
            ThermalProfile.Default     => 0x00,
            ThermalProfile.Performance => 0x01,
            ThermalProfile.Quiet       => 0x02,
            _                          => 0x00
        };
        try
        {
            await _ecService.WriteByteAsync(0x95, ecFanMode, ct);
            Console.WriteLine($"[PerformanceMode] EC 0x95 (Fan Profile) ← 0x{ecFanMode:X2} ✓");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PerformanceMode] EC 0x95 write failed: {ex.Message}");
        }

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

    private static readonly Guid ProcessorSubGroup = Guid.Parse("54533251-82be-4824-96C1-47B60B740D00");
    private static readonly Guid ProcessorBoost = Guid.Parse("BE337238-0D82-4146-A960-4F3749D470C7");
    private static readonly Guid HighPerformancePlan = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid BalancedPlan = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid PowerSaverPlan = Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a");

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, IntPtr schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint valueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, IntPtr schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint valueIndex);

    public void Dispose()
    {
        StopCountdownExtension();
    }
}
