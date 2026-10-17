using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol.Internal;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// Writes protocol frames to a transport stream.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Create(Stream, bool)"/> returns the writer that encodes frames to a stream. The
/// other writers decorate one: <see cref="ProtocolChannel.Writer"/> rejects an identifier outside
/// the channel's family, and the clients translate a closed transport into their own exceptions.
/// </para>
/// <para>
/// A derived writer implements <see cref="WriteFrameCoreAsync(ProtocolFrame, CancellationToken)"/>
/// and <see cref="FlushCoreAsync(CancellationToken)"/>, and overrides
/// <see cref="DisposeAsyncCore"/> when it owns a resource. The public members are not virtual:
/// they are the one entry point every caller goes through, and
/// <see cref="WriteFrameAsync(ProtocolFrame, CancellationToken)"/> refuses a payload longer than
/// <see cref="ProtocolFrameHeader.MaxPayloadLength"/> before any core runs.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class ProtocolFrameWriter : IAsyncDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolFrameWriter"/> class.
    /// </summary>
    /// <remarks>
    /// Protected, not private protected: the writers that decorate one live in Database.Client
    /// and Blob.Client as well as here.
    /// </remarks>
    protected ProtocolFrameWriter()
    {
    }

    /// <summary>
    /// Creates a frame writer over a stream.
    /// </summary>
    /// <param name="stream">The transport stream to write to.</param>
    /// <param name="leaveOpen">When true, the stream is not disposed with the writer.</param>
    /// <returns>The frame writer.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
    /// <remarks>
    /// The writer writes a frame's header and payload without flushing, so the caller batches
    /// small frames into one transport write with <see cref="FlushAsync(CancellationToken)"/>.
    /// It does not check message identifiers against a family; <see cref="ProtocolChannel"/>
    /// does.
    /// </remarks>
    public static ProtocolFrameWriter Create(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new ProtocolStreamFrameWriter(stream, leaveOpen);
    }

    /// <summary>
    /// Writes a frame to the transport.
    /// </summary>
    /// <param name="frame">The frame to write.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task that completes when the frame is written.</returns>
    /// <exception cref="ProtocolException">
    /// The payload is longer than <see cref="ProtocolFrameHeader.MaxPayloadLength"/>, thrown before
    /// the writer's core runs, or the core refused the frame (the channel's family check).
    /// </exception>
    /// <remarks>
    /// Every writer refuses an oversized payload here, the same way and before anything is
    /// written, whichever leaf it is (owner decision 29 of 2026-10-06, rule 4 of
    /// <c>database-area.md</c>): the bound is the envelope's, so no leaf may skip it. It runs before
    /// the core, so a frame that fails both the bound and <see cref="ProtocolChannel"/>'s family
    /// check reports the bound. The bound is thrown by the call itself, not through the returned
    /// task, as the channel's family check is.
    /// </remarks>
    public ValueTask WriteFrameAsync(ProtocolFrame frame, CancellationToken cancellationToken = default)
    {
        if ((uint)frame.Payload.Length > ProtocolFrameHeader.MaxPayloadLength)
        {
            throw new ProtocolException(
                $"Frame payload of {frame.Payload.Length} bytes exceeds the {ProtocolFrameHeader.MaxPayloadLength}-byte maximum.");
        }

        return WriteFrameCoreAsync(frame, cancellationToken);
    }

    /// <summary>
    /// Flushes buffered frames to the transport.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task that completes when the buffered frames are flushed.</returns>
    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        => FlushCoreAsync(cancellationToken);

    /// <summary>
    /// Releases the writer, and the transport stream when the writer owns it.
    /// </summary>
    /// <returns>A task that completes when the writer is released.</returns>
    public ValueTask DisposeAsync() => DisposeAsyncCore();

    /// <summary>
    /// Writes a frame to the transport.
    /// </summary>
    /// <param name="frame">The frame to write.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task that completes when the frame is written.</returns>
    /// <exception cref="ProtocolException">Thrown when the frame cannot be sent under the protocol framing.</exception>
    protected abstract ValueTask WriteFrameCoreAsync(ProtocolFrame frame, CancellationToken cancellationToken);

    /// <summary>
    /// Flushes buffered frames to the transport.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task that completes when the buffered frames are flushed.</returns>
    protected abstract ValueTask FlushCoreAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Releases what the writer owns. Called by <see cref="DisposeAsync"/>.
    /// </summary>
    /// <returns>A task that completes when the writer's resources are released.</returns>
    /// <remarks>The default body does nothing: a writer that owns nothing need not override it.</remarks>
    protected virtual ValueTask DisposeAsyncCore() => default;
}
