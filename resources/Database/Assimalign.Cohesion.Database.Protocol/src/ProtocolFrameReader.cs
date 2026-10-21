using System;
using System.IO;
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
    /// <param name="supportsBorrowedPayloads">Whether the leaf implements borrowed payload reads.</param>
    /// <remarks>
    /// Protected, not private protected: the readers that decorate one live in Database.Client
    /// and Blob.Client as well as here.
    /// </remarks>
    protected ProtocolFrameReader(bool supportsBorrowedPayloads = false)
    {
        SupportsBorrowedPayloads = supportsBorrowedPayloads;
    }

    /// <summary>Gets whether this reader supports pooled payloads borrowed within a synchronous callback.</summary>
    public bool SupportsBorrowedPayloads { get; }

    /// <summary>Reads a complete frame and synchronously consumes its reader-owned payload.</summary>
    /// <param name="consume">Consumes or decodes the frame before its pooled storage is returned.</param>
    /// <param name="cancellationToken">Cancellation token for the read.</param>
    /// <returns>True when a frame was consumed, or false at a clean end of stream.</returns>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    /// <exception cref="NotSupportedException">The reader does not support borrowed payloads.</exception>
    /// <exception cref="ProtocolException">The frame is malformed or incomplete.</exception>
    /// <remarks>The payload is valid only during <paramref name="consume"/>. Decode or copy everything
    /// that must survive before the callback returns. Storage is returned even if the callback throws,
    /// before the read completes. Reads and disposal must be serialized by the caller.
    /// <see cref="ReadFrameAsync"/> continues to return owned payloads.</remarks>
    public ValueTask<bool> ReadBorrowedFrameAsync(Action<ProtocolFrame> consume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consume);
        if (!SupportsBorrowedPayloads) { throw new NotSupportedException(ErrorMessages.BorrowedPayloadsUnsupported); }
        return ReadBorrowedFrameCoreAsync(consume, cancellationToken);
    }

    /// <summary>Reads a frame and consumes it while its pooled payload is borrowed.</summary>
    /// <param name="consume">Consumes the frame synchronously, without retaining its payload.</param>
    /// <param name="cancellationToken">Cancellation token for the read.</param>
    /// <returns>True when a frame was consumed, or false at a clean end of stream.</returns>
    /// <exception cref="NotSupportedException">The optional capability is unsupported.</exception>
    /// <remarks>A supporting leaf must release the payload after the callback, including when it throws.</remarks>
    protected virtual ValueTask<bool> ReadBorrowedFrameCoreAsync(Action<ProtocolFrame> consume, CancellationToken cancellationToken)
        => throw new NotSupportedException(ErrorMessages.BorrowedPayloadsUnsupported);

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
    public ValueTask<ProtocolFrame?> ReadFrameAsync(CancellationToken cancellationToken = default)
        => ReadFrameCoreAsync(cancellationToken);

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
}
