using System.Threading;
using System.Threading.Tasks;

namespace OmenFlow.Core.Interfaces;

/// <summary>
/// Provides direct Embedded Controller (EC) register Read/Write access via DeviceIoControl.
/// </summary>
public interface IEcService
{
    /// <summary>
    /// Reads a byte from the specified EC register.
    /// Safely handles unknown offsets (e.g., HP_EC_OFFSET_UNKNOWN for 8BBE) by returning a sentinel value instead of throwing.
    /// </summary>
    Task<byte> ReadByteAsync(byte register, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a byte to the specified EC register.
    /// </summary>
    Task WriteByteAsync(byte register, byte value, CancellationToken cancellationToken = default);
}
