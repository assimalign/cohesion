using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// HTTP/3 extended CONNECT tunnel (RFC 9220). Reads drain the lazy request body, which delivers a
/// CONNECT's <c>DATA</c> payloads as they arrive; writes frame <c>DATA</c> straight into the request
/// stream's output pipe and flush it, so QUIC's per-stream flow control paces them; closing completes the
/// output, the graceful FIN that ends the server's side (RFC 9114 §4.1).
/// </summary>
/// <remarks>
/// <para>
/// Writes go to the <see cref="PipeWriter"/> directly rather than through a stream adapter, so a peer
/// that stopped reading is observed: a flush that reports the reader completed, or that throws because
/// the peer aborted the stream, faults the write with an <see cref="IOException"/> instead of discarding
/// the octets.
/// </para>
/// <para>
/// The output admits one writer at a time — a <see cref="PipeWriter"/> tolerates no concurrency — and
/// the FIN must follow the last frame, so the head, every write, and the close take the same lock. A
/// frame is copied into the pipe whole before the flush, so cancelling a write's flush never splits a
/// frame.
/// </para>
/// </remarks>
internal sealed class Http3ExtendedConnectStream : HttpExtendedConnectStream
{
    private readonly Http3ConnectionContext _connection;
    private readonly Http3Context _context;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Set under _writeLock once the output is completed (the FIN).
    private volatile bool _writeCompleted;

    /// <summary>
    /// Initializes the tunnel for an accepted HTTP/3 extended CONNECT.
    /// </summary>
    /// <param name="connection">The connection the request stream belongs to.</param>
    /// <param name="context">The exchange whose request stream carries the tunnel.</param>
    public Http3ExtendedConnectStream(Http3ConnectionContext connection, Http3Context context)
    {
        _connection = connection;
        _context = context;
    }

    /// <summary>
    /// Gets whether the server's side of the request stream has ended (the FIN was written).
    /// </summary>
    public bool IsWriteCompleted => _writeCompleted;

    /// <summary>
    /// Writes the tunnel's response head as a HEADERS frame, without ending the stream.
    /// </summary>
    /// <param name="headerBlock">The QPACK-encoded <c>200</c> field section.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>A task that completes once the head is flushed.</returns>
    /// <exception cref="IOException">The request stream was reset, its peer stopped reading, or the connection closed.</exception>
    public async ValueTask WriteHeadAsync(byte[] headerBlock, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnwritable();
            await WriteFrameAsync(Http3FrameType.Headers, headerBlock, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    protected override ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => _context.RequestBody.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    protected override async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_writeCompleted)
            {
                throw new ObjectDisposedException(
                    nameof(Http3ExtendedConnectStream),
                    "The server's side of the extended CONNECT tunnel has already ended.");
            }

            ThrowIfUnwritable();
            await WriteFrameAsync(Http3FrameType.Data, data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async ValueTask CloseCoreAsync()
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_writeCompleted || _context.RequestBody.IsReset)
            {
                return;
            }

            _writeCompleted = true;

            // RFC 9114 §4.1 — the server's side of the stream ends with its FIN; completing the outbound
            // pipe is the IConnection contract's graceful half-close, and flushes anything a cancelled
            // write left buffered first.
            await _context.StreamConnection.Output.CompleteAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void ThrowIfUnwritable()
    {
        if (_context.RequestBody.IsReset)
        {
            throw new IOException("The HTTP/3 request stream carrying the extended CONNECT tunnel was reset.");
        }

        if (_connection.ConnectionClosed.IsCancellationRequested)
        {
            throw new IOException("The HTTP/3 connection carrying the extended CONNECT tunnel closed.");
        }
    }

    private async ValueTask WriteFrameAsync(Http3FrameType frameType, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        PipeWriter output = _context.StreamConnection.Output;
        FlushResult result;

        try
        {
            // RFC 9114 §7.1 — a frame is its type and length (variable-length integers) and its payload.
            Span<byte> header = output.GetSpan(2 * QuicVariableLengthInteger.MaxLength);
            int length = QuicVariableLengthInteger.Write(header, (long)frameType);
            length += QuicVariableLengthInteger.Write(header[length..], payload.Length);
            output.Advance(length);

            result = await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ConnectionException or InvalidOperationException)
        {
            // The in-memory driver surfaces a peer's abort as its reason; a pipe completed or disposed
            // underneath (a reset, the connection's teardown) throws InvalidOperationException. The QUIC
            // driver's QuicException is already an IOException.
            throw new IOException("The HTTP/3 request stream carrying the extended CONNECT tunnel failed.", exception);
        }

        if (result.IsCompleted)
        {
            // The peer stopped reading the stream (STOP_SENDING or a reset): the octets cannot reach it.
            throw new IOException("The peer stopped reading the HTTP/3 request stream carrying the extended CONNECT tunnel.");
        }
    }
}
