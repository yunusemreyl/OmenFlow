using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Models;

namespace OmenFlow.Core.Interfaces;

/// <summary>
/// Manages GPU MUX switches and TGP power presets.
/// </summary>
public interface IGpuService
{
    /// <summary>
    /// Gets the current GPU MUX mode.
    /// </summary>
    Task<GpuMuxMode?> GetMuxModeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches the GPU MUX mode (e.g., Hybrid, Discrete).
    /// </summary>
    Task<bool> SetMuxModeAsync(GpuMuxMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the GPU power preset (TGP limits).
    /// </summary>
    Task<bool> SetPowerPresetAsync(GpuPowerPreset preset, CancellationToken cancellationToken = default);
}
