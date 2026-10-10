using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

// The extended CONNECT tunnel (RFC 8441): the tunnel's 200 head, its DATA under the peer's send windows,
// the end of the server's side, and the exchange's finalization once the application's handler returns.
// The accept itself is the exchange control's (Http2ExchangeControl.AcceptTunnelAsync). The tunnel's
// reads need nothing here: they drain the stream's request body pipe, whose consumption credits the
// receive windows like any request body.
internal sealed partial class Http2ConnectionContext
{
    /// <summary>
    /// Gets whether the frame pump has exited, so no <c>WINDOW_UPDATE</c> can arrive and the connection
    /// can carry no more of a tunnel's octets.
    /// </summary>
    private bool IsSendCreditClosed
    {
        get
        {
            lock (_syncRoot)
            {
                return _sendCreditClosed;
            }
        }
    }

    /// <summary>
    /// Commits an accepted tunnel's response head: the prepared <c>200</c> field section in a HEADERS
    /// block <b>without</b> <c>END_STREAM</c>, so <c>DATA</c> can follow in both directions (RFC 8441
    /// §5). Holds the connection write gate for the whole HEADERS [+ CONTINUATION…] sequence
    /// (RFC 9113 §4.1).
    /// </summary>
    /// <param name="context">The exchange whose tunnel was accepted; its stream's response is already claimed.</param>
    /// <param name="cancellationToken">A token that cancels waiting for the write gate.</param>
    /// <exception cref="IOException">The stream was reset, or the connection closed, before the head was written.</exception>
    internal async Task WriteTunnelHeadAsync(Http2Context context, CancellationToken cancellationToken)
    {
        Http2Stream stream = context.Stream;
        byte[] headerBlock = HPackEncoder.EncodeResponseHeaders(context.Response.StatusCode, context.Response.Headers);

        await AcquireResponseWriteAsync(context, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsTunnelWritable(stream))
            {
                throw CreateTunnelUnwritableException(stream);
            }

            // The block is written in one piece, so a cancellation lands before or after it (#1326).
            await WriteHeaderBlockAsync(context.StreamId, headerBlock, endStream: false, cancellationToken).ConfigureAwait(false);
            await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeScheduler.Release();
        }
    }

    /// <summary>
    /// Sends tunnel octets as <c>DATA</c> frames split on the peer's <c>MAX_FRAME_SIZE</c>, each covered
    /// by credit from both the connection and the stream send window (RFC 9113 §5.2) and flushed at
    /// once. Waiting for credit — the peer's <c>WINDOW_UPDATE</c> — happens without the write gate, so a
    /// stalled tunnel holds back no other stream.
    /// </summary>
    /// <remarks>
    /// Unlike a streamed response, which discards the rest of a write once its stream is reset
    /// (RFC 9113 §5.4.2), a tunnel write faults: its octets never reached the peer, and the application
    /// must learn its tunnel is gone. Cancellation is honored while waiting for credit, for the gate, or
    /// for the transport to take a frame; each frame is written in one piece, so a cancellation never
    /// cuts one short (#1326).
    /// </remarks>
    /// <param name="context">The exchange whose tunnel writes.</param>
    /// <param name="data">The octets to send.</param>
    /// <param name="cancellationToken">A token that cancels waiting for credit, the write gate, or the transport.</param>
    /// <returns>A task that completes once every octet is on the wire.</returns>
    /// <exception cref="IOException">The stream was reset, or the connection closed.</exception>
    /// <exception cref="ObjectDisposedException">The end of the server's side was already written.</exception>
    internal async ValueTask WriteTunnelDataAsync(Http2Context context, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        Http2Stream stream = context.Stream;
        int offset = 0;

        while (offset < data.Length)
        {
            if (!IsTunnelWritable(stream))
            {
                throw CreateTunnelUnwritableException(stream);
            }

            int desired = Math.Min((int)_remoteSettings.MaxFrameSize, data.Length - offset);
            int granted = await AcquireSendWindowAsync(stream, desired, cancellationToken).ConfigureAwait(false);

            if (granted == 0)
            {
                // The stream can carry no more DATA: it was reset, or the end of its server side was written.
                throw CreateTunnelUnwritableException(stream);
            }

            bool gateHeld = false;
            try
            {
                await AcquireResponseWriteAsync(context, cancellationToken).ConfigureAwait(false);
                gateHeld = true;
            }
            finally
            {
                if (!gateHeld)
                {
                    // The wait for the gate was cancelled: the reserved credit never reached the wire.
                    ReturnSendWindow(stream, granted);
                }
            }

            try
            {
                // Re-checked under the gate: a reset, the end of the stream, or the loss of the connection
                // may have landed while this writer queued.
                if (!IsTunnelWritable(stream))
                {
                    ReturnSendWindow(stream, granted);
                    throw CreateTunnelUnwritableException(stream);
                }

                Http2Frame frame = new();
                frame.PrepareData(context.StreamId);
                await Http2FrameWriter.WriteAsync(Stream, frame, data.Slice(offset, granted), cancellationToken).ConfigureAwait(false);
                await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeScheduler.Release();
            }

            offset += granted;
        }
    }

    /// <summary>
    /// Ends the server's side of an accepted tunnel with an empty <c>DATA</c> frame carrying
    /// <c>END_STREAM</c> (RFC 8441 §5 — the orderly close, like a TCP FIN). The end is recorded under
    /// the write gate, so no <c>DATA</c> frame can follow it, and a writer parked on send-window credit
    /// is woken to observe it. Nothing is written for a stream that was reset or already ended.
    /// </summary>
    /// <param name="context">The exchange whose tunnel ends.</param>
    /// <returns>A task that completes once the end is on the wire.</returns>
    internal async ValueTask CompleteTunnelAsync(Http2Context context)
    {
        Http2Stream stream = context.Stream;

        await AcquireResponseWriteAsync(context, CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!stream.CanWriteResponse)
            {
                return;
            }

            Http2Frame frame = new();
            frame.PrepareData(context.StreamId);
            frame.DataFlags |= Http2DataFrameFlags.EndStream;
            await Http2FrameWriter.WriteAsync(Stream, frame, ReadOnlyMemory<byte>.Empty, CancellationToken.None).ConfigureAwait(false);
            await Stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);

            stream.CompleteResponse();
        }
        finally
        {
            _writeScheduler.Release();
        }

        stream.SendEndStream();
        WakeSendWindowWaiters();
    }

    /// <summary>
    /// Finalizes an exchange whose tunnel was accepted, in place of writing a response: the tunnel sent
    /// the exchange's only head.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><b>The exchange was cancelled, or the head never reached the wire</b> — the
    /// stream is reset with <c>CANCEL</c> (RFC 8441 §5: the abortive close, like a TCP RST), unless both
    /// sides had already ended it, which leaves only its removal.</description></item>
    /// <item><description><b>Otherwise</b> — the server's side is ended if the application left it open;
    /// the stream is then removed when the peer has ended its side too, or reset with <c>NO_ERROR</c>
    /// to stop a peer still sending and reclaim the stream's concurrency slot (RFC 9113 §8.1), as
    /// after any response that completes before its request.</description></item>
    /// </list>
    /// The after-response interceptor hooks do not run: the tunnel took the exchange over.
    /// </remarks>
    /// <param name="context">The exchange being finalized.</param>
    /// <param name="tunnel">The exchange's accepted tunnel.</param>
    /// <param name="cancellationToken">A token that abandons waiting for the end of the tunnel to be written.</param>
    /// <returns>A task that completes once the exchange's stream is finalized.</returns>
    private async ValueTask FinishTunnelAsync(Http2Context context, Http2ExtendedConnectStream tunnel, CancellationToken cancellationToken)
    {
        Http2Stream stream = context.Stream;

        if (context.CancelRequested || !tunnel.IsHeadCommitted)
        {
            tunnel.Abandon();

            if (stream.IsClosed)
            {
                await RemoveStreamAsync(context.StreamId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await EmitRstStreamAsync(context.StreamId, Http2ErrorCode.Cancel, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await tunnel.CloseAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

        if (stream.IsReset)
        {
            // Reset while the end was being written; the reset path already removed the stream.
            return;
        }

        await FinishCompletedResponseAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private bool IsTunnelWritable(Http2Stream stream) => stream.CanWriteResponse && !IsSendCreditClosed;

    private static Exception CreateTunnelUnwritableException(Http2Stream stream)
    {
        if (stream.IsReset)
        {
            return new IOException($"The HTTP/2 stream {stream.StreamId} carrying the extended CONNECT tunnel was reset.");
        }

        if (stream.IsResponseCompleted)
        {
            return new ObjectDisposedException(
                nameof(Http2ExtendedConnectStream),
                "The server's side of the extended CONNECT tunnel has already ended.");
        }

        return new IOException("The HTTP/2 connection carrying the extended CONNECT tunnel closed.");
    }
}
