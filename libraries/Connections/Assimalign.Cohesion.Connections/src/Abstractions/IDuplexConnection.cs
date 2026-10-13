using System.IO.Pipelines;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Represents a byte channel whose input receives peer bytes and whose output sends bytes to the peer.
/// </summary>
/// <remarks>
/// A connection is ready for I/O when returned by its listener or factory. Completing output
/// gracefully ends that direction; disposing the connection ends its lifetime.
/// </remarks>
public interface IDuplexConnection : IConnection, IDuplexPipe
{
    /// <summary>
    /// Gets which halves of the pipe are usable by the connection's owner.
    /// </summary>
    /// <remarks>
    /// Ordinary stream transports are bidirectional. A unidirectional multiplexed stream is
    /// read-only for its receiver and write-only for its opener.
    /// </remarks>
    ConnectionDirection Direction { get; }
}
