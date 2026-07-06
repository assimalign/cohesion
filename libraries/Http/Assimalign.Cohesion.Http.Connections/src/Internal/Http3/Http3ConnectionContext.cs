using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal.Http3.Frames;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3;

internal sealed class Http3ConnectionContext : HttpConnectionContext
{
    private readonly IMultiplexedConnection _connection;
    private readonly bool _isSecure;
    private readonly HttpServerLimits _limits;
    private readonly IHttpRequestInterceptor[] _interceptors;
    private readonly Http3PeerSettings _peerSettings = new();
    private readonly CancellationTokenSource _teardownSource = new();
    private IConnection? _controlStream;
    private Task? _peerControlDrainTask;
    private bool _controlStreamReceived;
    private bool _qpackEncoderStreamReceived;
    private bool _qpackDecoderStreamReceived;

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("osx")]
    public Http3ConnectionContext(IMultiplexedConnection connection, bool isSecure, HttpServerLimits limits, IHttpRequestInterceptor[] interceptors)
    {
        _connection = connection;
        _isSecure = isSecure;
        _limits = limits;
        _interceptors = interceptors;
    }

    public override EndPoint? LocalEndPoint => _connection.LocalEndPoint;
    public override EndPoint? RemoteEndPoint => _connection.RemoteEndPoint;

    /// <summary>
    /// Yields HTTP/3 request contexts for the lifetime of this connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before accepting any inbound stream the server opens its own outbound
    /// unidirectional control stream and sends SETTINGS as the first frame
    /// (RFC 9114 §6.2.1). That control stream stays open for the connection's
    /// lifetime — it is a critical stream — and is torn down connection-first
    /// (see <see cref="ShutdownAsync"/>). SETTINGS emission is best-effort: if
    /// the QUIC connection is already gone, the accept loop below observes the
    /// same failure and terminates.
    /// </para>
    /// <para>
    /// Failure handling is split between two scopes. Per-stream failures —
    /// truncated frames, malformed QPACK, varint overflow, a per-stream
    /// <see cref="IOException"/> — let the loop continue accepting more
    /// inbound streams on the same QUIC connection (HTTP/3 has no
    /// connection-level header table to corrupt, so a bad request on one
    /// stream is harmless to the others). Connection-terminating failures —
    /// the QUIC connection itself going away
    /// (<see cref="QuicException"/> or a contract-level
    /// <see cref="ConnectionException"/>), the multiplexed connection being
    /// disposed, or cancellation — exit the enumerable cleanly so the
    /// listener stays alive for the next peer.
    /// </para>
    /// </remarks>
    public override async IAsyncEnumerable<IHttpContext> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            // RFC 9114 §6.2.1 — each peer MUST open a control stream and send
            // SETTINGS as its first frame. Do this before (and independently
            // of) accepting request streams.
            await SendControlStreamSettingsAsync(cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                StreamAcceptOutcome accept = await TryAcceptInboundAsync(cancellationToken).ConfigureAwait(false);

                if (accept.TerminateConnection)
                {
                    yield break;
                }
                if (accept.StreamConnection is null)
                {
                    // Non-terminating accept failure is not expected, but guard
                    // anyway: skip and try the next inbound.
                    continue;
                }

                IConnection streamConnection = accept.StreamConnection;

                // RFC 9114 §6 — a bidirectional stream is a request stream; the
                // peer's unidirectional streams carry a type prefix (control,
                // QPACK encoder/decoder, push) and are demultiplexed separately.
                if (streamConnection.Direction != ConnectionDirection.Bidirectional)
                {
                    if (await TryHandleUnidirectionalStreamAsync(streamConnection, cancellationToken).ConfigureAwait(false))
                    {
                        // A control-stream protocol violation — duplicate control
                        // or QPACK stream, missing/!SETTINGS first frame, or a
                        // client-created push stream — is a connection error
                        // (RFC 9114 §6.2). Terminate the connection.
                        yield break;
                    }

                    continue;
                }

                Http3Context? context = await TryReadRequestAsync(streamConnection, cancellationToken).ConfigureAwait(false);

                if (context is not null)
                {
                    yield return context;
                }
            }
        }
        finally
        {
            await ShutdownAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens the server's outbound unidirectional control stream and writes the
    /// stream-type prefix (0x00) followed by a SETTINGS frame as its first frame
    /// (RFC 9114 §6.2.1 / §7.2.4). The stream is retained and deliberately left
    /// open — it is a critical stream (RFC 9114 §6.2.1) that must not be
    /// completed, aborted, or FIN'd until connection teardown, or a peer fails
    /// the connection with <c>H3_CLOSED_CRITICAL_STREAM</c>.
    /// </summary>
    /// <remarks>
    /// Best-effort: opening an outbound stream requires a live QUIC connection.
    /// If the connection is already gone the accept loop observes the same
    /// failure and terminates, so a setup failure here is swallowed rather than
    /// surfaced into the consumer's enumeration.
    /// </remarks>
    private async Task SendControlStreamSettingsAsync(CancellationToken cancellationToken)
    {
        if (_controlStream is not null)
        {
            return;
        }

        try
        {
            IConnection controlStream = await _connection
                .OpenStreamAsync(ConnectionDirection.WriteOnly, cancellationToken)
                .ConfigureAwait(false);
            _controlStream = controlStream;

            // Write the control-stream preamble directly to the outbound pipe.
            // PipeWriter.WriteAsync flushes, so the SETTINGS frame reaches the
            // transport as the stream's first bytes. The output is not completed
            // — the critical stream stays open for the connection lifetime.
            byte[] preamble = BuildControlStreamPreamble();
            await controlStream.Output.WriteAsync(preamble, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (QuicException)
        {
        }
        catch (ConnectionException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex) when (IsWireLevelFailure(ex))
        {
        }
    }

    /// <summary>
    /// Builds the bytes for the server control stream's opening: the RFC 9114
    /// §6.2 unidirectional stream-type prefix (0x00 = control) followed by a
    /// SETTINGS frame (type 0x04) carrying the server's advertised settings.
    /// </summary>
    private static byte[] BuildControlStreamPreamble()
    {
        using MemoryStream buffer = new();

        // RFC 9114 §6.2 — the control stream is identified by a stream-type
        // varint of 0x00 as its first bytes.
        QuicVariableLengthInteger.Write(buffer, Http3StreamType.Control);

        // RFC 9114 §6.2.1 / §7.2.4 — the first frame MUST be SETTINGS.
        byte[] settings = Http3LocalSettings.EncodePayload();
        QuicVariableLengthInteger.Write(buffer, (long)Http3FrameType.Settings);
        QuicVariableLengthInteger.Write(buffer, settings.Length);
        buffer.Write(settings, 0, settings.Length);

        return buffer.ToArray();
    }

    /// <summary>
    /// Tears the connection context down when the receive enumerable completes:
    /// signals the inbound control-stream drain to stop and waits for it to
    /// finish. The outbound control stream is deliberately not completed here —
    /// teardown stays connection-first, so the multiplexed connection's own
    /// disposal (via <see cref="Http3Connection.DisposeAsync"/>) closes the QUIC
    /// connection before releasing its streams, and a peer never observes
    /// <c>H3_CLOSED_CRITICAL_STREAM</c> ahead of <c>CONNECTION_CLOSE</c>.
    /// </summary>
    private async Task ShutdownAsync()
    {
        if (!_teardownSource.IsCancellationRequested)
        {
            _teardownSource.Cancel();
        }

        if (_peerControlDrainTask is not null)
        {
            // The drain swallows its own failures, so awaiting it here cannot
            // throw; it just lets the background loop unwind before teardown
            // completes.
            await _peerControlDrainTask.ConfigureAwait(false);
        }

        _teardownSource.Dispose();
    }

    /// <summary>
    /// Demultiplexes a peer-initiated unidirectional stream by its RFC 9114
    /// §6.2 stream-type prefix. Returns <see langword="true"/> when the stream
    /// is a connection-level protocol violation that must terminate the
    /// connection (duplicate control / QPACK stream, missing or non-SETTINGS
    /// first control frame, or a client-created push stream); otherwise
    /// <see langword="false"/>.
    /// </summary>
    private async Task<bool> TryHandleUnidirectionalStreamAsync(IConnection streamConnection, CancellationToken cancellationToken)
    {
        // RFC 9114 §6.2 — read directly off the stream connection's PipeReader
        // rather than the Stream adapter. Unidirectional streams are processed
        // incrementally (a varint stream-type prefix, then type-specific
        // frames), which the buffered ReadOnlySequence model expresses directly
        // without the adapter's read-size quirks.
        PipeReader reader = streamConnection.Input;

        long? streamType;
        try
        {
            streamType = await ReadVarintAsync(reader, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsPerStreamFailure(ex))
        {
            // The stream type could not be read — RFC 9114 §6.2 permits
            // abandoning an unparseable unidirectional stream without
            // affecting the connection.
            return false;
        }

        if (streamType is null)
        {
            return false;
        }

        switch (streamType.Value)
        {
            case Http3StreamType.Control:
                return await TryHandleControlStreamAsync(reader, cancellationToken).ConfigureAwait(false);

            case Http3StreamType.QPackEncoder:
                // RFC 9204 §4.2 — at most one encoder stream. With the dynamic
                // table disabled (QPACK_MAX_TABLE_CAPACITY = 0) it carries no
                // instructions to process, so accepting it is sufficient.
                if (_qpackEncoderStreamReceived)
                {
                    return true;
                }

                _qpackEncoderStreamReceived = true;
                return false;

            case Http3StreamType.QPackDecoder:
                if (_qpackDecoderStreamReceived)
                {
                    return true;
                }

                _qpackDecoderStreamReceived = true;
                return false;

            case Http3StreamType.Push:
                // RFC 9114 §6.2.2 — a client MUST NOT create a push stream;
                // treat it as H3_STREAM_CREATION_ERROR.
                return true;

            default:
                // RFC 9114 §6.2 — unknown unidirectional stream types are not
                // an error; the recipient may abandon them.
                return false;
        }
    }

    /// <summary>
    /// Reads and applies the peer's control stream. Enforces a single control
    /// stream and that its first frame is SETTINGS (RFC 9114 §6.2.1 / §7.2.4),
    /// then hands the stream to a background drain for the connection lifetime.
    /// Returns <see langword="true"/> on a protocol violation.
    /// </summary>
    private async Task<bool> TryHandleControlStreamAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        if (_controlStreamReceived)
        {
            // RFC 9114 §6.2.1 — only one control stream per peer.
            return true;
        }

        _controlStreamReceived = true;

        try
        {
            // RFC 9114 §6.2.1 / §7.2.4 — the first frame on the control stream
            // MUST be SETTINGS. Read and apply it synchronously so a missing or
            // non-SETTINGS opening frame terminates the connection inline.
            long? frameType = await ReadVarintAsync(reader, cancellationToken).ConfigureAwait(false);
            long? frameLength = frameType is null
                ? null
                : await ReadVarintAsync(reader, cancellationToken).ConfigureAwait(false);

            if (frameType is null || frameLength is null || frameType.Value != (long)Http3FrameType.Settings)
            {
                // Missing or non-SETTINGS first frame — H3_MISSING_SETTINGS.
                return true;
            }

            byte[] payload = await ReadExactAsync(reader, checked((int)frameLength.Value), cancellationToken).ConfigureAwait(false);
            ApplySettings(payload);
        }
        catch (Exception ex) when (IsPerStreamFailure(ex))
        {
            // The control stream is critical; a read/parse failure on the
            // mandatory SETTINGS frame is a connection error
            // (H3_FRAME_ERROR / H3_CLOSED_CRITICAL_STREAM).
            return true;
        }

        // RFC 9114 §6.2.1 — the control stream stays open for the connection
        // lifetime. Drain any post-SETTINGS frames on a background task so they
        // cannot accumulate unread in the pipe and so the accept loop is never
        // blocked waiting on the long-lived control stream.
        _peerControlDrainTask = DrainPeerControlStreamAsync(reader, cancellationToken);
        return false;
    }

    /// <summary>
    /// Drains post-SETTINGS frames from the peer's control stream for the
    /// connection lifetime, parsing and discarding each frame (RFC 9114 §7.2).
    /// GOAWAY (§7.2.6) and MAX_PUSH_ID (§7.2.7) are read but inert in this
    /// subset: GOAWAY-driven graceful drain is deferred, and the server never
    /// pushes, so MAX_PUSH_ID has no effect. Draining prevents unread control
    /// frames from accumulating in the pipe. The loop stops on end-of-stream,
    /// connection teardown, or a per-stream parse failure, and never throws
    /// into the receive loop.
    /// </summary>
    private async Task DrainPeerControlStreamAsync(PipeReader reader, CancellationToken receiveToken)
    {
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(receiveToken, _teardownSource.Token);
        CancellationToken cancellationToken = linked.Token;

        try
        {
            while (true)
            {
                long? frameType = await ReadVarintAsync(reader, cancellationToken).ConfigureAwait(false);
                if (frameType is null)
                {
                    // Clean end of the peer control stream — nothing more to drain.
                    break;
                }

                long? frameLength = await ReadVarintAsync(reader, cancellationToken).ConfigureAwait(false);
                if (frameLength is null)
                {
                    break;
                }

                await SkipAsync(reader, checked((int)frameLength.Value), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Connection teardown, or the receive token firing — stop draining.
        }
        catch (Exception ex) when (IsPerStreamFailure(ex))
        {
            // A malformed post-SETTINGS control frame. Strict HTTP/3 would treat
            // this as a connection error; in this parse-and-discard subset the
            // drain stops and connection teardown closes the QUIC connection.
        }
    }

    private void ApplySettings(byte[] payload)
    {
        int index = 0;
        while (index < payload.Length)
        {
            long identifier = QuicVariableLengthInteger.Decode(payload, ref index);
            long value = QuicVariableLengthInteger.Decode(payload, ref index);
            _peerSettings.Set(identifier, value);
        }
    }

    /// <summary>
    /// Reads a single QUIC variable-length integer off the pipe, buffering
    /// across reads until a complete integer is available. Returns
    /// <see langword="null"/> when the stream ends before any byte arrives.
    /// </summary>
    private static async Task<long?> ReadVarintAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (QuicVariableLengthInteger.TryDecode(buffer, out long value, out SequencePosition consumed))
            {
                reader.AdvanceTo(consumed);
                return value;
            }

            if (result.IsCompleted)
            {
                if (buffer.IsEmpty)
                {
                    // Clean end of stream before the next integer began.
                    reader.AdvanceTo(buffer.End);
                    return null;
                }

                // Bytes remain but cannot form a complete integer.
                reader.AdvanceTo(buffer.End);
                throw new EndOfStreamException("The QUIC variable-length integer was incomplete.");
            }

            // Not enough buffered yet; mark everything examined so the next
            // read waits for more bytes rather than returning the same span.
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="length"/> bytes off the pipe, buffering
    /// across reads. Throws <see cref="EndOfStreamException"/> when the stream
    /// ends first.
    /// </summary>
    private static async Task<byte[]> ReadExactAsync(PipeReader reader, int length, CancellationToken cancellationToken)
    {
        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.Length >= length)
            {
                ReadOnlySequence<byte> slice = buffer.Slice(0, length);
                byte[] payload = slice.ToArray();
                reader.AdvanceTo(slice.End);
                return payload;
            }

            if (result.IsCompleted)
            {
                reader.AdvanceTo(buffer.End);
                throw new EndOfStreamException("The HTTP/3 control SETTINGS frame was truncated.");
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>
    /// Reads and discards exactly <paramref name="length"/> bytes off the pipe,
    /// buffering across reads. Used to drain the payload of a post-SETTINGS
    /// control frame without allocating a buffer for bytes that are thrown away.
    /// Throws <see cref="EndOfStreamException"/> when the stream ends first.
    /// </summary>
    private static async Task SkipAsync(PipeReader reader, int length, CancellationToken cancellationToken)
    {
        int remaining = length;

        while (remaining > 0)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.Length >= remaining)
            {
                reader.AdvanceTo(buffer.GetPosition(remaining));
                return;
            }

            if (result.IsCompleted)
            {
                reader.AdvanceTo(buffer.End);
                throw new EndOfStreamException("An HTTP/3 control frame was truncated.");
            }

            remaining -= (int)buffer.Length;
            reader.AdvanceTo(buffer.End);
        }
    }

    /// <summary>
    /// Accepts the next inbound QUIC stream on this multiplexed connection.
    /// QUIC-level failures (peer aborted the connection, the multiplexed
    /// connection was disposed) and cancellation signal connection
    /// termination so the receive loop exits without throwing into the
    /// caller's <c>await foreach</c>.
    /// </summary>
    private async Task<StreamAcceptOutcome> TryAcceptInboundAsync(CancellationToken cancellationToken)
    {
        try
        {
            IConnection streamConnection = await _connection.AcceptStreamAsync(cancellationToken).ConfigureAwait(false);
            return new StreamAcceptOutcome(streamConnection, terminate: false);
        }
        catch (OperationCanceledException)
        {
            return new StreamAcceptOutcome(streamConnection: null, terminate: true);
        }
        catch (QuicException)
        {
            // The QUIC connection itself is gone — peer aborted, idle
            // timeout, or a transport-level error. No more streams will
            // arrive on this connection.
            return new StreamAcceptOutcome(streamConnection: null, terminate: true);
        }
        catch (ConnectionException)
        {
            // Contract-level abort/reset surfaced by the multiplexed
            // connection (ConnectionAbortedException / ConnectionResetException).
            return new StreamAcceptOutcome(streamConnection: null, terminate: true);
        }
        catch (ObjectDisposedException)
        {
            // The underlying multiplexed connection has been disposed
            // (cooperative shutdown raced this accept).
            return new StreamAcceptOutcome(streamConnection: null, terminate: true);
        }
        catch (Exception ex) when (IsWireLevelFailure(ex))
        {
            return new StreamAcceptOutcome(streamConnection: null, terminate: true);
        }
    }

    /// <summary>
    /// Reads a single request off the supplied QUIC stream. Any per-stream
    /// failure — truncated frames, malformed QPACK literal, overflow in a
    /// QUIC varint, an I/O error on this stream alone — is absorbed and
    /// returns <see langword="null"/>, signalling the caller to drop this
    /// stream and keep accepting more on the same QUIC connection.
    /// </summary>
    private async Task<Http3Context?> TryReadRequestAsync(IConnection streamConnection, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadRequestAsync(streamConnection, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation during a per-stream read is treated as
            // per-stream — the outer loop check on cancellationToken will
            // break out at the top of the next iteration if the cancel
            // applies to the whole connection.
            return null;
        }
        catch (Exception ex) when (IsPerStreamFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Wire-level transport failures that can surface from
    /// <c>AcceptStreamAsync</c>: low-level <see cref="IOException"/>,
    /// <see cref="System.Net.Sockets.SocketException"/>, and unexpected
    /// end-of-stream during the accept handshake. The QUIC connection is
    /// no longer usable when these fire.
    /// </summary>
    private static bool IsWireLevelFailure(Exception exception)
    {
        return exception is EndOfStreamException
            or IOException
            or System.Net.Sockets.SocketException;
    }

    /// <summary>
    /// Per-stream parse failures from the HTTP/3 frame reader and the
    /// QPACK literal decoder. These do not invalidate the QUIC connection
    /// — the offending stream is dropped and the receive loop keeps
    /// accepting subsequent streams.
    /// </summary>
    private static bool IsPerStreamFailure(Exception exception)
    {
        return exception is EndOfStreamException
            or InvalidDataException
            or NotSupportedException
            or OverflowException
            or ArgumentOutOfRangeException
            or IndexOutOfRangeException
            or IOException;
    }

    private readonly struct StreamAcceptOutcome
    {
        public StreamAcceptOutcome(IConnection? streamConnection, bool terminate)
        {
            StreamConnection = streamConnection;
            TerminateConnection = terminate;
        }

        public IConnection? StreamConnection { get; }
        public bool TerminateConnection { get; }
    }

    public override async ValueTask SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        if (context is not Http3Context http3Context)
        {
            throw new InvalidOperationException("The supplied context does not belong to an HTTP/3 connection.");
        }

        Stream stream = http3Context.StreamConnection.AsStream();
        byte[] bodyBytes = await ReadBodyAsync(http3Context.Response.Body, cancellationToken).ConfigureAwait(false);
        byte[] headerBlock = Http3HeaderCodec.EncodeResponseHeaders(http3Context, bodyBytes);

        await WriteFrameAsync(stream, Http3FrameType.Headers, headerBlock, cancellationToken).ConfigureAwait(false);

        if (bodyBytes.Length > 0)
        {
            await WriteFrameAsync(stream, Http3FrameType.Data, bodyBytes, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Http3Context?> ReadRequestAsync(IConnection streamConnection, CancellationToken cancellationToken)
    {
        Stream stream = streamConnection.AsStream();
        using MemoryStream requestBuffer = new();
        await stream.CopyToAsync(requestBuffer, cancellationToken).ConfigureAwait(false);

        byte[] requestBytes = requestBuffer.ToArray();
        byte[]? headerBlock = null;
        using MemoryStream body = new();
        int index = 0;

        while (index < requestBytes.Length)
        {
            long frameType = QuicVariableLengthInteger.Decode(requestBytes, ref index);
            long frameLength = QuicVariableLengthInteger.Decode(requestBytes, ref index);
            byte[] payload = requestBytes.AsSpan(index, checked((int)frameLength)).ToArray();
            index += checked((int)frameLength);

            switch ((Http3FrameType)frameType)
            {
                case Http3FrameType.Headers:
                    headerBlock = payload;
                    break;
                case Http3FrameType.Data:
                    await body.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        if (headerBlock is null)
        {
            return null;
        }

        byte[] bodyBytes = body.ToArray();
        Http3Request request = Http3HeaderCodec.DecodeRequestHeaders(headerBlock, _isSecure ? HttpScheme.Https : HttpScheme.Http, bodyBytes, out string? extendedConnectProtocol);
        Http3Response response = new();
        HttpConnectionInfo connectionInfo = new(streamConnection.LocalEndPoint, streamConnection.RemoteEndPoint);

        // Request-parse interceptor phase — the HTTP/3 analogue of the HTTP/1.1 invocation point,
        // run as the request head is assembled. RFC 9110 §9.3.6 — a CONNECT's post-head octets are
        // tunnel traffic, so its body hooks are skipped (head hooks still run). The hook-populated
        // feature collection flows into the exchange through the Http3Context features parameter;
        // zero interceptors keeps the pre-seam fast path.
        bool isConnect = request.Method == HttpMethod.Connect;
        HttpFeatureCollection? features;
        try
        {
            features = await HttpRequestInterceptorPipeline.InvokeAsync(
                _interceptors,
                HttpVersion.Http30,
                request,
                connectionInfo,
                _limits.MaxRequestBodySize,
                isConnect).ConfigureAwait(false);
        }
        catch (HttpRequestRejectedException)
        {
            // A request-parse interceptor refused this request (the pipeline already disposed the
            // partial body-wrapper chain and hook-attached features before surfacing). RFC 9114
            // §4.1 — abort the request stream (the ideal wire code is H3_REQUEST_REJECTED; the
            // IConnection abort contract resets with the transport's default stream error code)
            // rather than writing the h1-style status response. The QUIC connection and its other
            // streams are unaffected, so the caller drops just this stream and keeps serving.
            streamConnection.Abort();
            return null;
        }

        Http3Context context = new(request, response, connectionInfo, cancellationToken, streamConnection, features);

        // Surface the :protocol pseudo-header (RFC 8441 / RFC 9220) generically
        // so a higher layer (Assimalign.Cohesion.Http.ExtendedConnect) can model
        // extended CONNECT without the transport interpreting it. Same Items key
        // convention as the HTTP/2 transport.
        if (extendedConnectProtocol is not null)
        {
            context.Items[TransportItemKeys.Protocol] = extendedConnectProtocol;
        }

        return context;
    }

    private static async Task WriteFrameAsync(Stream stream, Http3FrameType frameType, byte[] payload, CancellationToken cancellationToken)
    {
        QuicVariableLengthInteger.Write(stream, (long)frameType);
        QuicVariableLengthInteger.Write(stream, payload.Length);

        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
        }
    }
    private static async Task<byte[]> ReadBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        if (body is MemoryStream memoryStream)
        {
            return memoryStream.ToArray();
        }

        using MemoryStream copy = new();
        if (body.CanSeek)
        {
            body.Position = 0;
        }

        await body.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy.ToArray();
    }
}
