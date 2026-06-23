using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Models;

namespace OmenFlow.Core.Interfaces;

public interface IPowerService
{
    Task<BatteryCareMode> GetBatteryCareModeAsync(CancellationToken cancellationToken = default);
    Task<bool> SetBatteryCareModeAsync(BatteryCareMode mode, CancellationToken cancellationToken = default);
}
