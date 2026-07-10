using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

/// <summary>
/// Safety monitor: automatically escalates the performance profile when Quiet mode
/// causes dangerously high CPU temperatures.
///
/// Problem (from OmenCore field reports):
///   Quiet mode aggressively limits fan speed and CPU power. On some workloads
///   (video export, compilation, VMs) the CPU can hit 95Â°C+ even in Quiet mode.
///   The user may not be watching the temperature â€” this monitor acts as a guardian.
///
/// Behavior (mirrors OmenCore's QuietSafetyMonitor):
///   - Only active when current thermal profile == Quiet
///   - If CPU temp exceeds EscalationThresholdC for EscalationDwellSeconds â†’ switch to Balanced
///   - Logs the escalation event prominently
///   - Does NOT automatically return to Quiet (user must do this manually)
///   - A "cooldown" prevents repeated rapid escalations within CooldownMinutes
///
/// Safety profile can be disabled per-user in Settings.
/// </summary>
public class QuietSafetyMonitor : BackgroundService
{
    private readonly PerformanceModeService _perfModeService;

    // Configurable thresholds
    public double EscalationThresholdC { get; set; } = 93.0;
    public int EscalationDwellSeconds  { get; set; } = 8;
    public bool IsEnabled              { get; set; } = true;
    public ThermalProfile EscalateTo   { get; set; } = ThermalProfile.Default;

    // Monitoring state
    private DateTime _overThresholdSince = DateTime.MinValue;
    private DateTime _lastEscalationAt   = DateTime.MinValue;
    private const int CooldownMinutes    = 5;
    private const int PollIntervalMs     = 3000;

    // Telemetry source (injected from Worker â€” same as FanCurveHostedService)
    public Func<WorkerTelemetry>? TelemetryProvider { get; set; }

    public QuietSafetyMonitor(PerformanceModeService perfModeService)
    {
        _perfModeService = perfModeService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        OmenFlow.Core.Services.Logger.LogInfo("[QuietSafety] Monitor started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollIntervalMs, stoppingToken);
                await EvaluateAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                OmenFlow.Core.Services.Logger.LogInfo($"[QuietSafety] Poll error: {ex.Message}");
            }
        }

        OmenFlow.Core.Services.Logger.LogInfo("[QuietSafety] Monitor stopped.");
    }

    private async Task EvaluateAsync(CancellationToken ct)
    {
        if (!IsEnabled) return;

        // Only act in Quiet mode
        var currentMode = await _perfModeService.GetCurrentModeAsync(ct);
        if (currentMode != ThermalProfile.Quiet)
        {
            // Reset dwell timer when not in Quiet mode
            _overThresholdSince = DateTime.MinValue;
            return;
        }

        // Read CPU temperature
        float cpuTemp = 0;
        if (TelemetryProvider != null)
        {
            cpuTemp = TelemetryProvider().CpuTemp;
        }

        if (cpuTemp <= 0) return; // No reading available

        // Check threshold
        if (cpuTemp >= EscalationThresholdC)
        {
            if (_overThresholdSince == DateTime.MinValue)
            {
                _overThresholdSince = DateTime.UtcNow;
                OmenFlow.Core.Services.Logger.LogInfo($"[QuietSafety] âš  CPU temp {cpuTemp}Â°C â‰¥ {EscalationThresholdC}Â°C in Quiet mode â€” starting escalation timer.");
                return;
            }

            double dwellSeconds = (DateTime.UtcNow - _overThresholdSince).TotalSeconds;

            if (dwellSeconds >= EscalationDwellSeconds)
            {
                // Check cooldown
                if ((DateTime.UtcNow - _lastEscalationAt).TotalMinutes < CooldownMinutes)
                {
                    OmenFlow.Core.Services.Logger.LogInfo($"[QuietSafety] Escalation suppressed â€” cooldown active ({CooldownMinutes}min).");
                    return;
                }

                await EscalateAsync(cpuTemp, dwellSeconds, ct);
            }
            else
            {
                OmenFlow.Core.Services.Logger.LogInfo($"[QuietSafety] CPU={cpuTemp}Â°C â€” waiting for dwell {dwellSeconds:F1}/{EscalationDwellSeconds}s before escalating...");
            }
        }
        else
        {
            // Back below threshold â€” reset dwell
            if (_overThresholdSince != DateTime.MinValue)
            {
                OmenFlow.Core.Services.Logger.LogInfo($"[QuietSafety] CPU temp {cpuTemp}Â°C dropped below threshold â€” resetting dwell timer.");
                _overThresholdSince = DateTime.MinValue;
            }
        }
    }

    private async Task EscalateAsync(double cpuTemp, double dwellSeconds, CancellationToken ct)
    {
        OmenFlow.Core.Services.Logger.LogInfo($"[QuietSafety] ğŸ”´ ESCALATING: CPU={cpuTemp}Â°C â‰¥ {EscalationThresholdC}Â°C for {dwellSeconds:F1}s in Quiet mode â†’ switching to {EscalateTo}");
        OmenFlow.Core.Services.Logger.LogInfo($"[QuietSafety] The profile will NOT automatically return to Quiet. User must change it manually.");

        _lastEscalationAt = DateTime.UtcNow;
        _overThresholdSince = DateTime.MinValue;

        try
        {
            bool ok = await _perfModeService.SetPerformanceModeAsync(EscalateTo, ct);
            OmenFlow.Core.Services.Logger.LogInfo(ok
                ? $"[QuietSafety] âœ“ Escalated to {EscalateTo} successfully."
                : $"[QuietSafety] âš  Escalation profile write failed.");
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[QuietSafety] Escalation error: {ex.Message}");
        }
    }
}

