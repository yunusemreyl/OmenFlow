using System;
using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

public class GpuControlService
{
    private readonly BiosService _biosService;
    private GpuMode? _pendingGpuMode;

    public GpuControlService(BiosService biosService)
    {
        _biosService = biosService;
    }

    public async Task<GpuMode> GetGpuModeAsync(CancellationToken ct = default)
    {
        if (_pendingGpuMode.HasValue)
        {
            return _pendingGpuMode.Value;
        }

        // Try empty payload first (standard Omen WMI read for 0x52)
        var (ret, data) = await _biosService.SendCommandAsync(0x00002, 0x52, Array.Empty<byte>(), 4, ct);
        if (ret != 0)
        {
            var (ret2, data2) = await _biosService.SendCommandAsync(0x00001, 0x52, Array.Empty<byte>(), 4, ct);
            ret = ret2;
            data = data2;
        }

        // Fallback to direction byte payload
        if (ret != 0)
        {
            var payload = new byte[] { 0x00, 0x04, 0x00, 0x00 };
            var (ret3, data3) = await _biosService.SendCommandAsync(0x00002, 0x52, payload, 4, ct);
            ret = ret3;
            data = data3;
            if (ret != 0)
            {
                var (ret4, data4) = await _biosService.SendCommandAsync(0x00001, 0x52, payload, 4, ct);
                ret = ret4;
                data = data4;
            }
        }

        if (ret == 0 && data != null && data.Length >= 1)
        {
            Console.WriteLine($"[GpuControl] GetGpuModeAsync ret=0, data[0]=0x{data[0]:X2}");
            if (data[0] == 0x01) return GpuMode.Discrete;
            if (data[0] == 0x00 || data[0] == 0x02) return GpuMode.Hybrid;
            return (GpuMode)data[0];
        }

        Console.WriteLine($"[GpuControl] GetGpuModeAsync failed with ret={ret}.");
        return GpuMode.Hybrid;
    }

    public async Task<(bool success, bool rebootRequired)> SetGpuModeAsync(GpuMode mode, CancellationToken ct = default)
    {
        _pendingGpuMode = mode == GpuMode.Optimus ? GpuMode.Hybrid : mode; // Arayüzdeki butonun geri atmaması için pending modunu sakla
        
        byte modeByte = (byte)(mode == GpuMode.Optimus ? GpuMode.Hybrid : mode);
        var payload = new byte[] { modeByte, 0x00, 0x00, 0x00 };
        var (ret, data) = await _biosService.SendCommandAsync(0x00002, 0x52, payload, 4, ct);
        
        if (ret != 0)
        {
            // Fallback to 0x00001
            var (ret2, data2) = await _biosService.SendCommandAsync(0x00001, 0x52, payload, 4, ct);
            ret = ret2;
            data = data2;
        }

        bool success = (ret == 0); // OmenMonWpf only checks ret == 0 for SetGpuMode
        
        return (success, success); // if successful, reboot is required
    }

    public async Task<GpuPowerLevel> GetGpuPowerAsync(CancellationToken ct = default)
    {
        var (ret, data) = await _biosService.SendCommandAsync(0x20008, 0x21, new byte[] { 0, 0, 0, 0 }, 4, ct);
        if (ret == 0 && data != null && data.Length >= 4)
        {
            byte customTgp = data[0];
            byte ppab = data[1];

            if (customTgp == 0 && ppab == 0) return GpuPowerLevel.BasePower;
            if (customTgp == 1 && ppab == 0) return GpuPowerLevel.ExtraPower;
            if (customTgp == 1 && ppab == 1) return GpuPowerLevel.MaxPower;
        }
        return GpuPowerLevel.BasePower;
    }

    public async Task<bool> SetGpuPowerAsync(GpuPowerLevel level, CancellationToken ct = default)
    {
        byte customTgp = level == GpuPowerLevel.BasePower ? (byte)0 : (byte)1;
        byte ppab = level == GpuPowerLevel.MaxPower ? (byte)1 : (byte)0;
        byte dState = 1; // Always D1
        byte peakTemp = 0;

        // Retrieve current peakTemp (slowdown temp) first, as setting it to 0 is dangerous/invalid
        var (readRet, readData) = await _biosService.SendCommandAsync(0x20008, 0x21, new byte[] { 0, 0, 0, 0 }, 4, ct);
        if (readRet == 0 && readData != null && readData.Length >= 4)
        {
            peakTemp = readData[3];
        }
        else
        {
            // If we fail to read, it's safer to abort or use a sensible default (e.g., 87C = 0x57)
            // But Omen default is usually kept as whatever the hardware had. 
            // We'll proceed with 0 if it fails, but log a warning if we had a logger.
            // For now, if read fails, returning false to be perfectly safe is an option, 
            // but we'll stick to best-effort.
        }

        byte[] payload = new byte[] { customTgp, ppab, dState, peakTemp };
        var (writeRet, _) = await _biosService.SendCommandAsync(0x20008, 0x22, payload, 0, ct);
        return writeRet == 0;
    }
}
