using System;
using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

public class KeyboardLightingService
{
    private readonly BiosService _biosService;
    private KeyboardType? _cachedType = null;

    public KeyboardLightingService(BiosService biosService)
    {
        _biosService = biosService;
    }

    public async Task<KeyboardType> DetectKeyboardTypeAsync(CancellationToken ct = default)
    {
        if (_cachedType.HasValue) return _cachedType.Value;

        var (ret, data) = await _biosService.SendCommandAsync(0x20008, 0x2B, new byte[4], 4, ct);
        if (ret == 0 && data != null && data.Length >= 1)
        {
            if (data[0] == 0x04) _cachedType = KeyboardType.FourZoneRgb;
            else if (data[0] == 0x05) _cachedType = KeyboardType.PerKeyRgb;
            else _cachedType = KeyboardType.Standard;
        }
        else
        {
            _cachedType = KeyboardType.Unknown;
        }

        return _cachedType.Value;
    }

    public async Task<(bool backlightOn, string zoneColors)> GetLightingAsync(CancellationToken ct = default)
    {
        var type = await DetectKeyboardTypeAsync(ct);
        if (type == KeyboardType.Unknown) return (false, "");

        if (type == KeyboardType.FourZoneRgb)
        {
            var (retColor, colorTable) = await _biosService.SendCommandAsync(0x20009, 0x02, new byte[4], 128, ct);
            if (retColor == 0 && colorTable != null && colorTable.Length >= 37)
            {
                byte[] zones = new byte[12];
                Array.Copy(colorTable, 25, zones, 0, 12);
                string base64 = Convert.ToBase64String(zones);

                bool isOn = false;
                for (int i = 0; i < 12; i++)
                {
                    if (zones[i] != 0) isOn = true;
                }
                return (isOn, base64);
            }
        }
        else // Standard
        {
            var (ret, data) = await _biosService.SendCommandAsync(0x20009, 0x04, new byte[] { 0x00, 0x00, 0x00, 0x00 }, 4, ct);
            if (ret == 0 && data != null && data.Length >= 1)
            {
                return (data[0] == 0xE4, "");
            }
        }

        return (false, "");
    }

    public async Task<bool> SetLightingAsync(bool backlightOn, string zoneColors = "", CancellationToken ct = default)
    {
        var type = await DetectKeyboardTypeAsync(ct);
        if (type == KeyboardType.Unknown) return false;

        if (type == KeyboardType.FourZoneRgb)
        {
            byte[] payload = new byte[128];
            payload[0] = 3; // 4 zones

            if (backlightOn)
            {
                byte[] zones = null;
                try { if (!string.IsNullOrEmpty(zoneColors)) zones = Convert.FromBase64String(zoneColors); } catch { }
                
                if (zones == null || zones.Length < 12)
                {
                    zones = new byte[12];
                    for (int i = 0; i < 12; i++) zones[i] = 255; // default white
                }
                Array.Copy(zones, 0, payload, 25, 12);
            }
            // else all 0s (black)

            var (ret, _) = await _biosService.SendCommandAsync(0x20009, 0x03, payload, 0, ct);
            return ret == 0;
        }
        else // Standard
        {
            byte cmdByte = (byte)(backlightOn ? 0xE4 : 0x64);
            var (ret, _) = await _biosService.SendCommandAsync(0x20009, 0x05, new byte[] { cmdByte, 0x00, 0x00, 0x00 }, 0, ct);
            return ret == 0;
        }
    }
}
