using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// Reads protocol frames from a transport stream.
/// </summary>
public interface IProtocolFrameReader : IAsyncDisposable
{
    /// <summary>
    /// Reads the next complete frame from the transport.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The next frame, or null when the transport completed gracefully.</returns>
    /// <exception cref="ProtocolException">Thrown when the incoming bytes violate the protocol framing.</exception>
    ValueTask<ProtocolFrame?> ReadFrameAsync(CancellationToken cancellationToken = default);
}
