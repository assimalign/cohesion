using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol.Internal;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// Reads protocol frames from a transport stream.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Create(Stream, bool)"/> returns the reader that decodes frames from a stream. The
/// other readers decorate one: <see cref="ProtocolChannel.Reader"/> rejects an identifier outside
/// the channel's family, and the clients translate a closed transport into their own exceptions.
/// </para>
/// <para>
/// A derived reader implements <see cref="ReadFrameCoreAsync(CancellationToken)"/>, and overrides
/// <see cref="DisposeAsyncCore"/> when it owns a resource. The public members are not virtual:
/// they are the one entry point every caller goes through.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class ProtocolFrameReader : IAsyncDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolFrameReader"/> class.
    /// </summary>
    /// <remarks>
    /// Protected, not private protected: the readers that decorate one live in Database.Client
    /// and Blob.Client as well as here.
    /// </remarks>
    protected ProtocolFrameReader()
    {
    }

    /// <summary>
    /// Creates a frame reader over a stream.
    /// </summary>
    /// <param name="stream">The transport stream to read from.</param>
    /// <param name="leaveOpen">When true, the stream is not disposed with the reader.</param>
    /// <returns>The frame reader.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
    /// <remarks>
    /// The reader validates each header's declared payload length before it allocates the
    /// payload, requires the whole declared payload, and tells a clean end of stream between
    /// frames apart from a truncated frame. It does not check message identifiers against a
    /// family; <see cref="ProtocolChannel"/> does.
    /// </remarks>
    public static ProtocolFrameReader Create(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new ProtocolStreamFrameReader(stream, leaveOpen);
    }

    /// <summary>
    /// Reads the next complete frame from the transport.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The next frame, or null when the transport completed gracefully.</returns>
    /// <exception cref="ProtocolException">Thrown when the incoming bytes violate the protocol framing.</exception>
    /// <remarks>
    /// The stream reader that <see cref="Create(Stream, bool)"/> returns writes each frame it reads to
    /// the <c>Assimalign.Cohesion.Database.Protocol</c> event source while a listener takes its frame
    /// trace. A decorating reader reads through the stream reader's public member, so it writes
    /// nothing itself: each wire frame is reported once.
    /// </remarks>
    public ValueTask<ProtocolFrame?> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        // Disabled, or a decorator: the core's task is returned as it is, so the trace costs one check.
        if (!ProtocolEventSource.Log.IsFrameTraceEnabled() || this is not ProtocolStreamFrameReader)
        {
            return ReadFrameCoreAsync(cancellationToken);
        }

        ValueTask<ProtocolFrame?> pending = ReadFrameCoreAsync(cancellationToken);
        if (pending.IsCompletedSuccessfully)
        {
            ProtocolFrame? frame = pending.Result;
            ProtocolEventSource.Log.FrameRead(frame);
            return new ValueTask<ProtocolFrame?>(frame);
        }

        return ReadFrameTracedAsync(pending);
    }

    /// <summary>
    /// Releases the reader, and the transport stream when the reader owns it.
    /// </summary>
    /// <returns>A task that completes when the reader is released.</returns>
    public ValueTask DisposeAsync() => DisposeAsyncCore();

    /// <summary>
    /// Reads the next complete frame from the transport.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The next frame, or null when the transport completed gracefully.</returns>
    /// <exception cref="ProtocolException">Thrown when the incoming bytes violate the protocol framing.</exception>
    protected abstract ValueTask<ProtocolFrame?> ReadFrameCoreAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Releases what the reader owns. Called by <see cref="DisposeAsync"/>.
    /// </summary>
    /// <returns>A task that completes when the reader's resources are released.</returns>
    /// <remarks>The default body does nothing: a reader that owns nothing need not override it.</remarks>
    protected virtual ValueTask DisposeAsyncCore() => default;

    /// <summary>
    /// Awaits a read that did not complete synchronously and writes its frame to the frame trace.
    /// Entered only while a listener takes the trace.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<ProtocolFrame?> ReadFrameTracedAsync(ValueTask<ProtocolFrame?> pending)
    {
        ProtocolFrame? frame = await pending.ConfigureAwait(false);
        ProtocolEventSource.Log.FrameRead(frame);
        return frame;
    }
}
