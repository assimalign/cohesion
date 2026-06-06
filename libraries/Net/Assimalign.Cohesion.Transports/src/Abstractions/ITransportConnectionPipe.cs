using System.IO;
using System.IO.Pipelines;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// Allows for data to be sent and received from a <see cref="ITransportConnection"/>.
/// </summary>
/// <remarks>
/// Is derived from <see cref="IDuplexPipe"/> which can be used to write to and read from.
/// </remarks>
public interface ITransportConnectionPipe : IDuplexPipe
{
    /// <summary>
    /// Gets the <see cref="ITransportConnectionPipe"/> to a <see cref="System.IO.Stream"/>.
    /// </summary>
    /// <returns></returns>
    Stream Stream { get; }
}
