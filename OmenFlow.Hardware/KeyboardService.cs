using System;
using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Interfaces;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

public class KeyboardService : IKeyboardService
{
    private readonly IBiosService _biosService;

    public KeyboardService(IBiosService biosService)
    {
        _biosService = biosService;
    }

    public async Task<bool> SetZoneColorAsync(KeyboardZone zone, RgbColor color, CancellationToken cancellationToken = default)
    {
        // 1. Query keyboard type
        var (ret, kbdData) = await _biosService.SendCommandAsync(0x20008, 0x2B, new byte[4], 4, cancellationToken);
        if (ret != 0 || kbdData == null || kbdData.Length == 0) return false;

        if (kbdData[0] == 0x04)
        {
            // 4-zone RGB keyboard
            var (retGet, colorTable) = await _biosService.SendCommandAsync(0x20009, 0x02, new byte[128], 128, cancellationToken);
            if (retGet != 0 || colorTable == null || colorTable.Length < 128) return false;

            colorTable[0] = 3; // zone count

            int offset = 25 + ((int)zone * 3);
            if (offset + 2 < colorTable.Length)
            {
                colorTable[offset] = color.R;
                colorTable[offset + 1] = color.G;
                colorTable[offset + 2] = color.B;
            }

            var (retSet, _) = await _biosService.SendCommandAsync(0x20009, 0x03, colorTable, 0, cancellationToken);
            return retSet == 0;
        }
        else
        {
            // Standard keyboard fallback
            bool isOn = color.R > 0 || color.G > 0 || color.B > 0;
            byte value = isOn ? (byte)0xE4 : (byte)0x64;

            var (retSet, _) = await _biosService.SendCommandAsync(0x20009, 0x05, new byte[] { value, 0, 0, 0 }, 0, cancellationToken);
            return retSet == 0;
        }
    }
}
