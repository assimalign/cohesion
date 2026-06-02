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

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http3;

using Assimalign.Cohesion.Http.Transports.Internal.Http3.Frames;

internal sealed class Http3ConnectionContext : HttpConnectionContext
{
    private static readonly ITransportConnectionPipe DisabledPipe = new TransportConnectionPipe(Stream.Null);
    private readonly IMultiplexTransportConnection _connection;
    private readonly Dictionary<string, object?> _items;
    private readonly Func<IHttpFeatureCollection>? _createFeatures;
    private readonly bool _isSecure;
    private readonly Http3PeerSettings _peerSettings = new();
    private bool _controlStreamReceived;
    private bool _qpackEncoderStreamReceived;
    private bool _qpackDecoderStreamReceived;
    private EndPoint _localEndPoint;
    private EndPoint _remoteEndPoint;

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("osx")]
    public Http3ConnectionContext(
        IMultiplexTransportConnection connection,
        bool isSecure,
        Func<IHttpFeatureCollection>? createFeatures)
    {
        _connection = connection;
        _isSecure = isSecure;
        _items = new Dictionary<string, object?>(StringComparer.Ordinal);
        _createFeatures = createFeatures;
        _localEndPoint = connection is QuicTransportConnection quicConnection
            ? quicConnection.LocalEndPoint
            : new IPEndPoint(IPAddress.None, 0);
        _remoteEndPoint = connection is QuicTransportConnection quicConnection2
            ? quicConnection2.RemoteEndPoint
            : new IPEndPoint(IPAddress.None, 0);
    }

    public override EndPoint LocalEndPoint => _localEndPoint;

    public override EndPoint RemoteEndPoint => _remoteEndPoint;

    public override ITransportConnectionPipe Pipe => DisabledPipe;

    public override IDictionary<string, object?> Items => _items;

    /// <summary>
    /// Yields HTTP/3 request contexts for the lifetime of this connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Failure handling is split between two scopes. Per-stream failures —
    /// truncated frames, malformed QPACK, varint overflow, a per-stream
    /// <see cref="IOException"/> — let the loop continue accepting more
    /// inbound streams on the same QUIC connection (HTTP/3 has no
    /// connection-level header table to corrupt, so a bad request on one
    /// stream is harmless to the others). Connection-terminating failures —
    /// the QUIC connection itself going away
    /// (<see cref="QuicException"/>), the multiplex transport being
    /// disposed, or cancellation — exit the enumerable cleanly so the
    /// listener stays alive for the next peer.
    /// </para>
    /// </remarks>
    public override async IAsyncEnumerable<IHttpContext> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            StreamAcceptOutcome accept = await TryAcceptInboundAsync(cancellationToken).ConfigureAwait(false);
            if (accept.TerminateConnection)
            {
                yield break;
            }
            if (accept.StreamContext is null)
            {
                // Non-terminating accept failure is not expected, but guard
                // anyway: skip and try the next inbound.
                continue;
            }

            ITransportConnectionContext streamContext = accept.StreamContext;
            _localEndPoint = streamContext.LocalEndPoint;
            _remoteEndPoint = streamContext.RemoteEndPoint;

            // RFC 9114 §6 — a bidirectional stream is a request stream; the
            // peer's unidirectional streams carry a type prefix (control,
            // QPACK encoder/decoder, push) and are demultiplexed separately.
            if (!streamContext.IsBidirectional)
            {
                if (await TryHandleUnidirectionalStreamAsync(streamContext, cancellationToken).ConfigureAwait(false))
                {
                    // A control-stream protocol violation — duplicate control
                    // or QPACK stream, missing/!SETTINGS first frame, or a
                    // client-created push stream — is a connection error
                    // (RFC 9114 §6.2). Terminate the connection.
                    yield break;
                }

                continue;
            }

            Http3Context? context = await TryReadRequestAsync(streamContext, cancellationToken).ConfigureAwait(false);

            if (context is not null)
            {
                yield return context;
            }
        }
    }

    /// <summary>
    /// Demultiplexes a peer-initiated unidirectional stream by its RFC 9114
    /// §6.2 stream-type prefix. Returns <see langword="true"/> when the stream
    /// is a connection-level protocol violation that must terminate the
    /// connection (duplicate control / QPACK stream, missing or non-SETTINGS
    /// first control frame, or a client-created push stream); otherwise
    /// <see langword="false"/>.
    /// </summary>
    private async Task<bool> TryHandleUnidirectionalStreamAsync(ITransportConnectionContext streamContext, CancellationToken cancellationToken)
    {
        // RFC 9114 §6.2 — read directly off the PipeReader rather than the
        // Stream adapter. Unidirectional streams are processed incrementally
        // (a varint stream-type prefix, then type-specific frames), which the
        // buffered ReadOnlySequence model expresses directly without the
        // adapter's read-size quirks.
        PipeReader reader = streamContext.Pipe.Input;

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
    /// stream and that its first frame is SETTINGS (RFC 9114 §6.2.1 / §7.2.4).
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
            // MUST be SETTINGS. The frame is read and applied; later control
            // frames are not acted on in this supported subset.
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
            return false;
        }
        catch (Exception ex) when (IsPerStreamFailure(ex))
        {
            // The control stream is critical; a read/parse failure is a
            // connection error (H3_FRAME_ERROR / H3_CLOSED_CRITICAL_STREAM).
            return true;
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
    /// Accepts the next inbound QUIC stream on this multiplex connection.
    /// QUIC-level failures (peer aborted the connection, the multiplex was
    /// disposed) and cancellation signal connection termination so the
    /// receive loop exits without throwing into the caller's
    /// <c>await foreach</c>.
    /// </summary>
    private async Task<StreamAcceptOutcome> TryAcceptInboundAsync(CancellationToken cancellationToken)
    {
        try
        {
            ITransportConnectionContext streamContext = await _connection.OpenInboundAsync(cancellationToken).ConfigureAwait(false);
            return new StreamAcceptOutcome(streamContext, terminate: false);
        }
        catch (OperationCanceledException)
        {
            return new StreamAcceptOutcome(streamContext: null, terminate: true);
        }
        catch (QuicException)
        {
            // The QUIC connection itself is gone — peer aborted, idle
            // timeout, or a transport-level error. No more streams will
            // arrive on this connection.
            return new StreamAcceptOutcome(streamContext: null, terminate: true);
        }
        catch (ObjectDisposedException)
        {
            // The underlying multiplex transport has been disposed
            // (cooperative shutdown raced this accept).
            return new StreamAcceptOutcome(streamContext: null, terminate: true);
        }
        catch (Exception ex) when (IsWireLevelFailure(ex))
        {
            return new StreamAcceptOutcome(streamContext: null, terminate: true);
        }
    }

    /// <summary>
    /// Reads a single request off the supplied QUIC stream. Any per-stream
    /// failure — truncated frames, malformed QPACK literal, overflow in a
    /// QUIC varint, an I/O error on this stream alone — is absorbed and
    /// returns <see langword="null"/>, signalling the caller to drop this
    /// stream and keep accepting more on the same QUIC connection.
    /// </summary>
    private async Task<Http3Context?> TryReadRequestAsync(ITransportConnectionContext streamContext, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadRequestAsync(streamContext, cancellationToken).ConfigureAwait(false);
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
    /// <c>OpenInboundAsync</c>: low-level <see cref="IOException"/>,
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
        public StreamAcceptOutcome(ITransportConnectionContext? streamContext, bool terminate)
        {
            StreamContext = streamContext;
            TerminateConnection = terminate;
        }

        public ITransportConnectionContext? StreamContext { get; }
        public bool TerminateConnection { get; }
    }

    public override async ValueTask SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        if (context is not Http3Context http3Context)
        {
            throw new InvalidOperationException("The supplied context does not belong to an HTTP/3 connection.");
        }

        Stream stream = http3Context.StreamContext.Pipe.GetStream();
        byte[] bodyBytes = await ReadBodyAsync(http3Context.Response.Body, cancellationToken).ConfigureAwait(false);
        byte[] headerBlock = Http3HeaderCodec.EncodeResponseHeaders(http3Context, bodyBytes);

        await WriteFrameAsync(stream, Http3FrameType.Headers, headerBlock, cancellationToken).ConfigureAwait(false);

        if (bodyBytes.Length > 0)
        {
            await WriteFrameAsync(stream, Http3FrameType.Data, bodyBytes, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Http3Context?> ReadRequestAsync(ITransportConnectionContext streamContext, CancellationToken cancellationToken)
    {
        Stream stream = streamContext.Pipe.GetStream();
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
        HttpConnectionInfo connectionInfo = new(streamContext.LocalEndPoint, streamContext.RemoteEndPoint, _isSecure);

        Http3Context context = new(request, response, connectionInfo, cancellationToken, streamContext, _createFeatures?.Invoke());

        // RFC 9220 — model a valid extended CONNECT explicitly as a feature.
        if (extendedConnectProtocol is not null)
        {
            context.Features.Set(new HttpExtendedConnectFeature(extendedConnectProtocol));
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
