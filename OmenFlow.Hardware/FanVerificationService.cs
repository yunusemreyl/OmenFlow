using System;
using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

/// <summary>
/// Verifies that fan commands were successfully applied by reading back RPM after a short delay.
/// 
/// Mirrors OmenCore's FanVerificationService pattern:
/// - After each SetFanLevel or SetMaxFan command, waits briefly then reads RPM.
/// - Compares actual RPM against expected range derived from the target percent.
/// - Logs a warning if actual RPM is outside acceptable bounds.
/// - Does NOT retry automatically; verification is diagnostic/informational only.
/// 
/// RPM bounds are deliberately wide (Â±35%) to account for:
/// - Thermal load variation affecting actual fan speed
/// - LUT quantization differences between fan firmware generations
/// - Brief spin-up delays on V1 EC hardware
/// </summary>
public class FanVerificationService
{
    private readonly FanControlService _fanControlService;
    private readonly BoardConfiguration _boardConfig;

    // How long to wait after a command before reading back RPM.
    // V1 EC models need ~1.5s to settle; V2 WMI is faster but we use the same window.
    private const int VerificationDelayMs = 1800;

    // Acceptable RPM band: Â±35% of expected.
    // Wide range accounts for ambient temp variation and LUT rounding.
    private const double RpmToleranceFactor = 0.35;

    // Estimated max RPM per fan type (conservative â€” actual hardware may be higher)
    private const int EstimatedMaxCpuRpm = 6000;
    private const int EstimatedMaxGpuRpm = 6200;

    // Track consecutive verification failures to detect stuck fans
    private int _consecutiveFailures = 0;
    private const int FailureAlarmThreshold = 3;

    public FanVerificationService(FanControlService fanControlService, BoardConfiguration boardConfig)
    {
        _fanControlService = fanControlService;
        _boardConfig = boardConfig;
    }

    /// <summary>
    /// Fires-and-forgets a verification check after a fan command.
    /// Does not block the caller.
    /// </summary>
    /// <param name="targetPercent">The percent that was commanded (0-100).</param>
    /// <param name="isModeSwitch">True if this was a mode switch (verification uses wider tolerance).</param>
    public void VerifyAfterCommandAsync(int targetPercent, bool isModeSwitch = false)
    {
        _ = DoVerifyAsync(targetPercent, isModeSwitch, CancellationToken.None);
    }

    /// <summary>
    /// Awaitable verification check â€” returns true if RPM is within expected range.
    /// </summary>
    public async Task<bool> VerifyAsync(int targetPercent, bool isModeSwitch = false, CancellationToken ct = default)
    {
        return await DoVerifyAsync(targetPercent, isModeSwitch, ct);
    }

    private async Task<bool> DoVerifyAsync(int targetPercent, bool isModeSwitch, CancellationToken ct)
    {
        // Skip verification on models without EC readback capability
        if (!_boardConfig.SupportsFanControlEc && !_boardConfig.SupportsFanCurves)
        {
            OmenFlow.Core.Services.Logger.LogInfo("[FanVerify] Skipping â€” model does not support EC readback.");
            return true;
        }

        try
        {
            // Wait for fan hardware to respond to the command
            int delay = isModeSwitch ? VerificationDelayMs * 2 : VerificationDelayMs;
            await Task.Delay(delay, ct);

            var (cpuRpm, gpuRpm) = await _fanControlService.GetFanRpmAsync(ct);

            // If both 0: fan may be in BIOS auto or truly stalled
            if (cpuRpm == 0 && gpuRpm == 0)
            {
                if (targetPercent == 0)
                {
                    OmenFlow.Core.Services.Logger.LogInfo("[FanVerify] âœ“ RPM=0 matches target=0% (BIOS auto).");
                    _consecutiveFailures = 0;
                    return true;
                }

                _consecutiveFailures++;
                OmenFlow.Core.Services.Logger.LogInfo($"[FanVerify] âš  Both fans report 0 RPM after commanding {targetPercent}% (failure #{_consecutiveFailures})");
                if (_consecutiveFailures >= FailureAlarmThreshold)
                {
                    OmenFlow.Core.Services.Logger.LogInfo($"[FanVerify] â›” {_consecutiveFailures} consecutive zero-RPM verifications. Possible stuck fan or EC communication issue.");
                }
                return false;
            }

            // At max mode (100%), just confirm fans are spinning fast
            if (targetPercent >= 95)
            {
                bool fastEnough = cpuRpm > EstimatedMaxCpuRpm * 0.55 || gpuRpm > EstimatedMaxGpuRpm * 0.55;
                if (fastEnough)
                {
                    OmenFlow.Core.Services.Logger.LogInfo($"[FanVerify] âœ“ Max mode: CPU={cpuRpm}RPM, GPU={gpuRpm}RPM â€” fans spinning.");
                    _consecutiveFailures = 0;
                    return true;
                }
            }

            // Calculate expected RPM range from target percent
            double tolerance = isModeSwitch ? RpmToleranceFactor * 1.5 : RpmToleranceFactor;
            int expectedCpuRpm = (int)(EstimatedMaxCpuRpm * targetPercent / 100.0);
            int expectedGpuRpm = (int)(EstimatedMaxGpuRpm * targetPercent / 100.0);
            int cpuLow  = (int)(expectedCpuRpm * (1.0 - tolerance));
            int cpuHigh = (int)(expectedCpuRpm * (1.0 + tolerance));
            int gpuLow  = (int)(expectedGpuRpm * (1.0 - tolerance));
            int gpuHigh = (int)(expectedGpuRpm * (1.0 + tolerance));

            bool cpuOk = cpuRpm == 0 || (cpuRpm >= cpuLow && cpuRpm <= cpuHigh);
            bool gpuOk = gpuRpm == 0 || _boardConfig.FanCount < 2 || (gpuRpm >= gpuLow && gpuRpm <= gpuHigh);

            if (cpuOk && gpuOk)
            {
                OmenFlow.Core.Services.Logger.LogInfo($"[FanVerify] âœ“ Target={targetPercent}% â†’ CPU={cpuRpm}RPM (exp={expectedCpuRpm}), GPU={gpuRpm}RPM (exp={expectedGpuRpm})");
                _consecutiveFailures = 0;
                return true;
            }

            _consecutiveFailures++;
            OmenFlow.Core.Services.Logger.LogInfo($"[FanVerify] âš  RPM mismatch at target={targetPercent}%: " +
                              $"CPU={cpuRpm} (exp {cpuLow}-{cpuHigh}), " +
                              $"GPU={gpuRpm} (exp {gpuLow}-{gpuHigh}) " +
                              $"[failure #{_consecutiveFailures}]");
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[FanVerify] Verification error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Returns a summary of recent verification state for diagnostics.
    /// </summary>
    public string GetVerificationSummary()
    {
        return $"ConsecutiveFailures={_consecutiveFailures}, AlarmThreshold={FailureAlarmThreshold}";
    }
}

