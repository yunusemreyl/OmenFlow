using System.Threading;
using System.Threading.Tasks;

namespace OmenFlow.Core.Interfaces;

public interface ICpuTurboService
{
    Task<bool> IsTurboEnabledAsync(CancellationToken cancellationToken = default);
    Task<bool> SetTurboEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}
