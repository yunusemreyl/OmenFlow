using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Models;

namespace OmenFlow.Core.Interfaces;

public interface IPerformanceModeService
{
    Task<bool> SetPerformanceModeAsync(ThermalProfile mode, CancellationToken ct = default);
    Task<ThermalProfile> GetCurrentModeAsync(CancellationToken ct = default);
}
