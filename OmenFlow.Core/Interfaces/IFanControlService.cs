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
    /// Sets the manual fan level for both CPU and GPU fans simultaneously.
    /// </summary>
    Task<bool> SetFanLevelAsync(int percent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets CPU and GPU fans to independent speeds.
    /// Falls back to unified level on single-fan or WMI-only models.
    /// </summary>
    Task<bool> SetFanLevelIndependentAsync(int cpuPercent, int gpuPercent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Toggles the maximum fan speed override (command 0x27).
    /// </summary>
    Task<bool> SetMaxFanAsync(bool enable, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores the BIOS default automatic fan control sequence.
    /// </summary>
    Task<bool> RestoreAutoControlAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Notifies the service that a fan mode transition is starting.
    /// Callers that change fan presets should call this so RPM reads during
    /// the BIOS reset window are not exposed to the UI as "0 RPM failure".
    /// </summary>
    void NotifyFanTransitionStarted();

    /// <summary>
    /// True when a fan mode transition window is active (RPM reads may be unreliable).
    /// </summary>
    bool IsFanTransitioning { get; }
}

