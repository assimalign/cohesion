using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// Writes protocol frames to a transport stream.
/// </summary>
public interface IProtocolFrameWriter : IAsyncDisposable
{
    /// <summary>
    /// Writes a frame to the transport.
    /// </summary>
    /// <param name="frame">The frame to write.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    ValueTask WriteFrameAsync(ProtocolFrame frame, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flushes buffered frames to the transport.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    ValueTask FlushAsync(CancellationToken cancellationToken = default);
}
