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
    /// Tells the hosted service whether Max Fan Mode is active, so it can continuously reapply it.
    /// </summary>
    void SetMaxModeActive(bool isActive);
}
