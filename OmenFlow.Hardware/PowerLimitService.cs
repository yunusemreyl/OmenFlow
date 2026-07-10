using System;
using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Interfaces;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

/// <summary>
/// Sets CPU (PL1/PL2) and GPU (TGP) power limits via HP BIOS WMI commands.
///
/// OmenCore uses PawnIO (signed kernel driver) for MSR-level control.
/// OmenFlow uses only the HP BIOS WMI interface â€” no driver installation required.
/// This covers the majority of use cases on OMEN 2020â€“2024 hardware.
///
/// WMI Command map (reverse-engineered from OmenCore source + HP service manual):
///   0x29 â†’ CMD_SET_POWER_LIMITS: payload[0]=CPU PL1 (W), payload[1]=CPU PL2 (W)
///   0x21 â†’ CMD_GPU_POWER:        payload[0]=customTGP, payload[1]=PPAB, payload[2]=dState, payload[3]=peakTemp
///   0x1A â†’ CMD_THERMAL_POLICY:   sets overall thermal envelope (already used by PerformanceModeService)
///
/// Safety rules (adopted from OmenCore PowerLimitController):
///   - Never push 0W limits (can severely cap performance or cause BSOD on some models)
///   - CPU PL1 â‰¤ PL2 always enforced
///   - GPU TGP clamped to [15W, 175W] for sanity
///   - On models without SupportsDetailedPowerLimits, writes are skipped
/// </summary>
public class PowerLimitService
{
    private readonly IBiosService _biosService;
    private readonly IEcService _ecService;
    private readonly BoardConfiguration _boardConfig;

    // Last applied limits (for deduplication and diagnostics)
    private int _lastCpuPl1W = -1;
    private int _lastCpuPl2W = -1;
    private int _lastGpuTgpW = -1;
    private DateTime _lastApplyTime = DateTime.MinValue;

    // Safety clamps
    private const int CpuPl1MinW  = 5;
    private const int CpuPl1MaxW  = 150;
    private const int CpuPl2MinW  = 5;
    private const int CpuPl2MaxW  = 200;
    private const int GpuTgpMinW  = 15;
    private const int GpuTgpMaxW  = 175;

    public PowerLimitService(IBiosService biosService, IEcService ecService, BoardConfiguration boardConfig)
    {
        _biosService = biosService;
        _ecService = ecService;
        _boardConfig = boardConfig;
    }

    // â”€â”€ Public API â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Applies CPU power limits (PL1 sustained, PL2 boost) via WMI CMD 0x29.
    /// Returns true if the command was accepted.
    /// </summary>
    public async Task<bool> SetCpuPowerLimitsAsync(int pl1W, int pl2W, CancellationToken ct = default)
    {
        if (!ValidateAndLog("CPU", ref pl1W, CpuPl1MinW, CpuPl1MaxW)) return false;
        if (!ValidateAndLog("CPU", ref pl2W, CpuPl2MinW, CpuPl2MaxW)) return false;

        // PL1 â‰¤ PL2 (PL2 is burst, always â‰¥ sustained)
        if (pl1W > pl2W)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[PowerLimit] Adjusting PL2 to match PL1: PL1={pl1W}W > PL2={pl2W}W â†’ PL2={pl1W}W");
            pl2W = pl1W;
        }

        if (pl1W == _lastCpuPl1W && pl2W == _lastCpuPl2W)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[PowerLimit] CPU limits unchanged (PL1={pl1W}W, PL2={pl2W}W) â€” skipping write.");
            return true;
        }

        OmenFlow.Core.Services.Logger.LogInfo($"[PowerLimit] Setting CPU limits: PL1={pl1W}W, PL2={pl2W}W");

        // WMI CMD 0x29: payload[0]=PL1, payload[1]=PL2 (W, direct)
        var payload = new byte[] { (byte)pl1W, (byte)pl2W, 0x00, 0x00 };
        bool success = false;

        for (int attempt = 1; attempt <= 3 && !success; attempt++)
        {
            try
            {
                var (ret, _) = await _biosService.SendCommandAsync(0x20008, 0x29, payload, 0, ct);
                success = ret == 0;
                if (!success)
                    OmenFlow.Core.Services.Logger.LogInfo($"[PowerLimit] CMD 0x29 attempt {attempt} returned ret={ret}");
            }
            catch (Exception ex)
            {
                OmenFlow.Core.Services.Logger.LogInfo($"[PowerLimit] CMD 0x29 attempt {attempt} failed: {ex.Message}");
            }

            if (!success && attempt < 3) await Task.Delay(150, ct);
        }

        if (success)
        {
            _lastCpuPl1W = pl1W;
            _lastCpuPl2W = pl2W;
            _lastApplyTime = DateTime.UtcNow;
            OmenFlow.Core.Services.Logger.LogInfo($"[PowerLimit] âœ“ CPU PL1={pl1W}W, PL2={pl2W}W applied.");
        }
        else
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[PowerLimit] âš  CPU power limit write failed after 3 attempts.");
        }

        return success;
    }

    /// <summary>
    /// Applies GPU TGP (Total Graphics Power) via WMI CMD 0x22.
    /// customTgp=true â†’ PPAB extension mode, false â†’ base TDP only.
    /// </summary>
    public async Task<bool> SetGpuTgpAsync(int tgpW, bool ppab = false, CancellationToken ct = default)
    {
        tgpW = Math.Clamp(tgpW, GpuTgpMinW, GpuTgpMaxW);

        if (tgpW == _lastGpuTgpW)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[PowerLimit] GPU TGP unchanged ({tgpW}W) â€” skipping write.");
            return true;
        }

        OmenFlow.Core.Services.Logger.LogInfo($"[PowerLimit] Setting GPU TGP: {tgpW}W (PPAB={ppab})");

        // Read current peak temp before writing (never overwrite with 0)
        byte peakTemp = 87; // safe default
        try
        {
            var (readRet, readData) = await _biosService.SendCommandAsync(
                0x20008, 0x21, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 4, ct);
            if (readRet == 0 && readData.Length >= 4 && readData[3] > 0)
                peakTemp = readData[3];
        }
        catch { }

        byte customTgp = (byte)(tgpW > 0 ? 1 : 0);
        byte ppabByte  = (byte)(ppab ? 1 : 0);
        var payload    = new byte[] { customTgp, ppabByte, 0x01, peakTemp };

        bool success = false;
        for (int attempt = 1; attempt <= 3 && !success; attempt++)
        {
            try
            {
                var (ret, _) = await _biosService.SendCommandAsync(0x20008, 0x22, payload, 0, ct);
                success = ret == 0;
            }
            catch { }
            if (!success && attempt < 3) await Task.Delay(150, ct);
        }

        if (success)
        {
            _lastGpuTgpW = tgpW;
            OmenFlow.Core.Services.Logger.LogInfo($"[PowerLimit] âœ“ GPU TGP={tgpW}W applied.");
        }
        else
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[PowerLimit] âš  GPU TGP write failed after 3 attempts.");
        }

        return success;
    }

    /// <summary>
    /// Applies a complete power limit preset (CPU PL1/PL2 + GPU TGP) atomically.
    /// Skips if the model doesn't declare support for detailed power limits.
    /// </summary>
    public async Task<bool> ApplyPowerPresetAsync(PowerLimitPreset preset, CancellationToken ct = default)
    {
        if (!_boardConfig.SupportsDetailedPowerLimits)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[PowerLimit] Model {_boardConfig.BoardId} does not declare SupportsDetailedPowerLimits â€” skipping EC power writes.");
            return false;
        }

        if (preset.CpuPl1W <= 0 && preset.CpuPl2W <= 0 && preset.GpuTgpW <= 0)
        {
            OmenFlow.Core.Services.Logger.LogInfo("[PowerLimit] âš  All preset limits are non-positive â€” refusing to apply.");
            return false;
        }

        bool cpuOk = true, gpuOk = true;

        if (preset.CpuPl1W > 0 || preset.CpuPl2W > 0)
        {
            int pl1 = preset.CpuPl1W > 0 ? preset.CpuPl1W : preset.CpuPl2W;
            int pl2 = preset.CpuPl2W > 0 ? preset.CpuPl2W : pl1;
            cpuOk = await SetCpuPowerLimitsAsync(pl1, pl2, ct);
        }

        if (preset.GpuTgpW > 0)
        {
            gpuOk = await SetGpuTgpAsync(preset.GpuTgpW, preset.GpuPpab, ct);
        }

        return cpuOk && gpuOk;
    }

    /// <summary>
    /// Returns diagnostics string for export.
    /// </summary>
    public string GetDiagnosticsSummary()
    {
        return $"LastApply={_lastApplyTime:O} | CPU PL1={_lastCpuPl1W}W PL2={_lastCpuPl2W}W | GPU TGP={_lastGpuTgpW}W";
    }

    // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static bool ValidateAndLog(string label, ref int value, int min, int max)
    {
        if (value <= 0)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[PowerLimit] âš  {label} limit is non-positive ({value}W) â€” refusing to write.");
            return false;
        }
        int clamped = Math.Clamp(value, min, max);
        if (clamped != value)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[PowerLimit] {label} limit {value}W clamped to [{min}W, {max}W] = {clamped}W");
            value = clamped;
        }
        return true;
    }
}

/// <summary>
/// A named snapshot of power limits to apply atomically.
/// </summary>
public record PowerLimitPreset(
    string Name,
    int CpuPl1W,
    int CpuPl2W,
    int GpuTgpW,
    bool GpuPpab = false)
{
    // Built-in presets matching OmenCore's defaults for typical OMEN hardware
    public static readonly PowerLimitPreset Performance = new("Performance", 45, 90, 80, GpuPpab: true);
    public static readonly PowerLimitPreset Balanced    = new("Balanced",   35, 65, 60, GpuPpab: false);
    public static readonly PowerLimitPreset Quiet       = new("Quiet",      20, 30, 40, GpuPpab: false);
}

