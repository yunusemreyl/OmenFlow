using System.Threading;
using System.Threading.Tasks;

namespace OmenFlow.Core.Interfaces;

/// <summary>
/// Provides low-level access to the HP WMI CimSession interface.
/// </summary>
public interface IBiosService
{
    /// <summary>
    /// Sends a command to the BIOS via WMI.
    /// </summary>
    /// <param name="commandType">The command type (e.g., 0x20008, 0x20009).</param>
    /// <param name="command">The command byte (e.g., 0x1A, 0x2E).</param>
    /// <param name="inData">The payload to send. Must not be null.</param>
    /// <param name="outSize">The expected size of the output data, used for method name bucketing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple containing the return code and the output byte array.</returns>
    Task<(int ReturnCode, byte[] OutData)> SendCommandAsync(uint commandType, byte command, byte[] inData, int outSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets both CPU and GPU temperatures via WMI (Command 0x23).
    /// </summary>
    Task<(int cpuTemp, int gpuTemp)?> GetBothTemperaturesAsync(CancellationToken cancellationToken = default);
}
