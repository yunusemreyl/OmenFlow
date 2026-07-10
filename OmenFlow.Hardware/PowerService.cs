using System;
using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Interfaces;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

public class PowerService : IPowerService
{
    private readonly IBiosService _biosService;
    private const uint CMD_BATTERY_CARE = 0x24;
    private const uint BIOS_CMD_DEFAULT = 0x20008;

    public PowerService(IBiosService biosService)
    {
        _biosService = biosService;
    }

    public async Task<BatteryCareMode> GetBatteryCareModeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _biosService.SendCommandAsync(BIOS_CMD_DEFAULT, (byte)CMD_BATTERY_CARE, new byte[4], 4, cancellationToken);
            if (result.OutData != null && result.OutData.Length >= 1)
            {
                return (BatteryCareMode)result.OutData[0];
            }
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[ERROR] GetBatteryCareModeAsync failed: {ex.Message}");
        }
        return BatteryCareMode.Disabled;
    }

    public async Task<bool> SetBatteryCareModeAsync(BatteryCareMode mode, CancellationToken cancellationToken = default)
    {
        try
        {
            var data = new byte[4];
            data[0] = (byte)mode;
            data[1] = 0x00;
            data[2] = 0x00;
            data[3] = 0x00;

            var result = await _biosService.SendCommandAsync(BIOS_CMD_DEFAULT, (byte)CMD_BATTERY_CARE, data, 0, cancellationToken);
            return result.ReturnCode == 0;
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[ERROR] SetBatteryCareModeAsync failed: {ex.Message}");
            return false;
        }
    }
}

