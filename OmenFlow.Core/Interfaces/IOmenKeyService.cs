using System.Threading.Tasks;

namespace OmenFlow.Core.Interfaces;

public interface IOmenKeyService
{
    bool IsInterceptEnabled { get; set; }
    void SaveConfig();
    Task SetInterceptEnabledAsync(bool enabled);
}
