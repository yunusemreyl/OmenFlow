using System.Threading.Tasks;
using OmenFlow.Core.Models;

namespace OmenFlow.Core.Interfaces;

/// <summary>
/// Manages continuous application of custom fan curves.
/// </summary>
public interface IFanCurveService
{
    /// <summary>
    /// Submits a new fan curve to be continuously applied by the background hosted service.
    /// Replaces any currently active curve atomically.
    /// </summary>
    /// <param name="curve">The fan curve to apply, or null to revert to default BIOS control.</param>
    Task ApplyCustomCurveAsync(FanCurve? curve);

    /// <summary>
    /// Submits independent CPU and GPU fan curves. When active, each fan is driven
    /// by its own temperature source and curve points.
    /// Pass null for both to revert to BIOS auto control.
    /// </summary>
    Task ApplyIndependentCurvesAsync(FanCurve? cpuCurve, FanCurve? gpuCurve);

    /// <summary>
    /// Tells the hosted service whether Max Fan Mode is active, so it can continuously reapply it.
    /// </summary>
    void SetMaxModeActive(bool isActive);

    /// <summary>
    /// Signals a system suspend/resume transition.
    /// When active=true, curve engine pauses to prevent EC interaction during low-power states.
    /// When active=false (resume), the engine resumes and immediately reapplies the active curve.
    /// </summary>
    void SetSuspendActive(bool active);

    /// <summary>True when a custom curve or max mode is actively being applied.</summary>
    bool IsCurveOrHoldActive { get; }
}

