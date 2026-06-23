using System;
using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Interfaces;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

public class GpuService : IGpuService
{
    private readonly IBiosService _biosService;

    public GpuService(IBiosService biosService)
    {
        _biosService = biosService;
    }

    public async Task<GpuMuxMode?> GetMuxModeAsync(CancellationToken cancellationToken = default)
    {
        var (ret, outData) = await _biosService.SendCommandAsync(0x00002, 0x52, Array.Empty<byte>(), 4, cancellationToken);
        if (ret != 0)
        {
            // Fallback
            (ret, outData) = await _biosService.SendCommandAsync(0x00001, 0x52, Array.Empty<byte>(), 4, cancellationToken);
        }

        if (ret == 0 && outData != null && outData.Length >= 1)
        {
            return (GpuMuxMode)outData[0];
        }

        return null;
    }

    public async Task<bool> SetMuxModeAsync(GpuMuxMode mode, CancellationToken cancellationToken = default)
    {
        byte[] inData = new byte[] { (byte)mode, 0, 0, 0 };
        var (ret, _) = await _biosService.SendCommandAsync(0x00002, 0x52, inData, 0, cancellationToken);
        return ret == 0;
    }

    public async Task<bool> SetPowerPresetAsync(GpuPowerPreset preset, CancellationToken cancellationToken = default)
    {
        // Stub: implement GPU power preset (TGP limits) switching here
        // var (ret, _) = await _biosService.SendCommandAsync(0x..., new[] { preset.PresetId }, 0, cancellationToken);
        // return ret == 0;

        throw new NotImplementedException("GPU power preset switching not implemented.");
    }
}
