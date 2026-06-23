using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Models;

namespace OmenFlow.Core.Interfaces;

/// <summary>
/// Manages high-level fan control, thermal profiles, and write-then-readback verification.
/// </summary>
public interface IFanControlService
{
    Task<(int CpuFanRpm, int GpuFanRpm)> GetFanRpmAsync(CancellationToken ct = default);

    Task<int> GetCpuTemperatureAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the current thermal profile from cache.
    /// </summary>
    Task<ThermalProfile> GetThermalProfileAsync(CancellationToken cancellationToken = default);


    /// <summary>
    /// Sets the manual fan level for the system. Implements write-then-readback verification.
    /// </summary>
    Task<bool> SetFanLevelAsync(int percent, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Toggles the maximum fan speed override (command 0x27).
    /// </summary>
    Task<bool> SetMaxFanAsync(bool enable, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores the BIOS default automatic fan control sequence.
    /// </summary>
    Task<bool> RestoreAutoControlAsync(CancellationToken cancellationToken = default);
}
