using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Models;

namespace OmenFlow.Core.Interfaces;

/// <summary>
/// Controls per-zone RGB keyboard lighting.
/// </summary>
public interface IKeyboardService
{
    /// <summary>
    /// Sets the color for a specific keyboard zone.
    /// </summary>
    Task<bool> SetZoneColorAsync(KeyboardZone zone, RgbColor color, CancellationToken cancellationToken = default);
}
