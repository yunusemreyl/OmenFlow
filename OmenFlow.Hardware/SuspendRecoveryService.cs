using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using OmenFlow.Core.Interfaces;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

/// <summary>
/// Monitors Windows power mode changes (suspend/resume) and coordinates fan + performance
/// mode recovery after the system wakes up.
///
/// Behavior (mirrors OmenCore's critical fix from v3.8.2):
/// - On Suspend: stops Max Fan keepalive timer unconditionally to prevent fans-stuck-at-max
///   and failed lid-close/standby issues. Pauses the curve engine.
/// - On Resume: re-applies the last active fan curve/mode and performance profile so
///   hardware state matches what the user set before suspend.
///
/// Safety: if the resume restore throws, it is swallowed and logged â€” we never let
/// a failed restore block the application from starting normally after wakeup.
/// </summary>
public class SuspendRecoveryService : BackgroundService
{
    private readonly IFanControlService _fanControlService;
    private readonly IFanCurveService _fanCurveService;
    private readonly IPerformanceModeService _perfModeService;

    // Snapshot of state captured just before suspend
    private int _preSuspendFanMode = 0;
    private FanCurve? _preSuspendCurve = null;
    private FanCurve? _preSuspendCpuCurve = null;
    private FanCurve? _preSuspendGpuCurve = null;
    private ThermalProfile _preSuspendProfile = ThermalProfile.Default;
    private bool _suspendEventReceived = false;
    private readonly SemaphoreSlim _resumeLock = new(1, 1);

    // Delegate to retrieve current fan state (injected by Worker)
    public Func<int>? GetCurrentFanMode { get; set; }
    public Func<FanCurve?>? GetCurrentCurve { get; set; }
    public Func<(FanCurve? cpu, FanCurve? gpu)>? GetCurrentIndependentCurves { get; set; }

    public SuspendRecoveryService(
        IFanControlService fanControlService,
        IFanCurveService fanCurveService,
        IPerformanceModeService perfModeService)
    {
        _fanControlService = fanControlService;
        _fanCurveService = fanCurveService;
        _perfModeService = perfModeService;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // SystemEvents must be subscribed on a thread with a message pump.
        // We run a minimal STA thread for this purpose.
        var thread = new Thread(() =>
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            OmenFlow.Core.Services.Logger.LogInfo("[SuspendRecovery] PowerModeChanged hook registered.");

            // Keep the thread alive until cancellation
            while (!stoppingToken.IsCancellationRequested)
            {
                Thread.Sleep(1000);
            }

            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            OmenFlow.Core.Services.Logger.LogInfo("[SuspendRecovery] PowerModeChanged hook removed.");
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return Task.CompletedTask;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                HandleSuspend();
                break;
            case PowerModes.Resume:
                // Fire-and-forget resume restore (cannot await in event handler)
                _ = HandleResumeAsync();
                break;
        }
    }

    private void HandleSuspend()
    {
        OmenFlow.Core.Services.Logger.LogInfo("[SuspendRecovery] System suspending â€” capturing state snapshot.");

        try
        {
            // Capture current state before suspend
            _preSuspendFanMode = GetCurrentFanMode?.Invoke() ?? 0;
            _preSuspendCurve = GetCurrentCurve?.Invoke();
            var (cpu, gpu) = GetCurrentIndependentCurves?.Invoke() ?? (null, null);
            _preSuspendCpuCurve = cpu;
            _preSuspendGpuCurve = gpu;
            _preSuspendProfile = _perfModeService.GetCurrentModeAsync().GetAwaiter().GetResult();

            OmenFlow.Core.Services.Logger.LogInfo($"[SuspendRecovery] Snapshot: FanMode={_preSuspendFanMode}, Curve={(_preSuspendCurve != null ? "active" : "none")}, Profile={_preSuspendProfile}");
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[SuspendRecovery] Failed to capture state snapshot: {ex.Message}");
        }

        // CRITICAL (OmenCore v3.8.2 fix): Stop Max Fan keepalive BEFORE suspend.
        // If keepalive keeps running during suspend, fans stay at max â†’ BIOS thermal shutdown.
        try
        {
            _fanCurveService.SetSuspendActive(true);
            OmenFlow.Core.Services.Logger.LogInfo("[SuspendRecovery] Curve engine paused. System is safe to suspend.");
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[SuspendRecovery] Error pausing curve engine: {ex.Message}");
        }

        _suspendEventReceived = true;
    }

    private async Task HandleResumeAsync()
    {
        OmenFlow.Core.Services.Logger.LogInfo("[SuspendRecovery] System resuming â€” waiting before restore...");

        // Brief delay to allow BIOS/ACPI to stabilize after resume before we issue WMI commands
        await Task.Delay(TimeSpan.FromSeconds(3));

        if (!await _resumeLock.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            OmenFlow.Core.Services.Logger.LogInfo("[SuspendRecovery] Resume restore skipped â€” already in progress.");
            return;
        }

        try
        {
            // Resume curve engine first (this triggers an immediate re-apply)
            _fanCurveService.SetSuspendActive(false);

            if (!_suspendEventReceived)
            {
                OmenFlow.Core.Services.Logger.LogInfo("[SuspendRecovery] No suspend event captured; skipping explicit restore.");
                return;
            }

            OmenFlow.Core.Services.Logger.LogInfo($"[SuspendRecovery] Restoring: FanMode={_preSuspendFanMode}, Profile={_preSuspendProfile}");

            // Restore performance profile
            await RestoreProfileSafeAsync();

            // Restore fan mode
            await RestoreFanModeSafeAsync();

            _suspendEventReceived = false;
            OmenFlow.Core.Services.Logger.LogInfo("[SuspendRecovery] âœ“ Post-resume restore complete.");
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[SuspendRecovery] Resume restore error: {ex.Message}");
        }
        finally
        {
            _resumeLock.Release();
        }
    }

    private async Task RestoreProfileSafeAsync()
    {
        try
        {
            await _perfModeService.SetPerformanceModeAsync(_preSuspendProfile);
            OmenFlow.Core.Services.Logger.LogInfo($"[SuspendRecovery] âœ“ Performance profile restored: {_preSuspendProfile}");
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[SuspendRecovery] Failed to restore performance profile: {ex.Message}");
        }
    }

    private async Task RestoreFanModeSafeAsync()
    {
        try
        {
            switch (_preSuspendFanMode)
            {
                case 2: // Max Fan
                    _fanCurveService.SetMaxModeActive(true);
                    await _fanControlService.SetMaxFanAsync(true);
                    OmenFlow.Core.Services.Logger.LogInfo("[SuspendRecovery] âœ“ Max Fan mode restored.");
                    break;

                case 1: // OmenFlow preset / custom curve
                case 3:
                    _fanCurveService.SetMaxModeActive(false);
                    if (_preSuspendCpuCurve != null && _preSuspendGpuCurve != null)
                    {
                        await _fanCurveService.ApplyIndependentCurvesAsync(_preSuspendCpuCurve, _preSuspendGpuCurve);
                        OmenFlow.Core.Services.Logger.LogInfo("[SuspendRecovery] âœ“ Independent CPU+GPU curves restored.");
                    }
                    else if (_preSuspendCurve != null)
                    {
                        await _fanCurveService.ApplyCustomCurveAsync(_preSuspendCurve);
                        OmenFlow.Core.Services.Logger.LogInfo("[SuspendRecovery] âœ“ Fan curve restored.");
                    }
                    else
                    {
                        // Curve data missing â€” fall back to BIOS auto
                        await _fanControlService.RestoreAutoControlAsync();
                        OmenFlow.Core.Services.Logger.LogInfo("[SuspendRecovery] Fan curve data unavailable; restored BIOS auto control.");
                    }
                    break;

                default: // Auto
                    _fanCurveService.SetMaxModeActive(false);
                    await _fanCurveService.ApplyCustomCurveAsync(null);
                    await _fanControlService.RestoreAutoControlAsync();
                    OmenFlow.Core.Services.Logger.LogInfo("[SuspendRecovery] âœ“ Auto fan mode restored.");
                    break;
            }
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[SuspendRecovery] Failed to restore fan mode: {ex.Message}");
        }
    }
}

