using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Transports.Internal.Http2.HPack;
using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http2;

internal sealed class Http2ConnectionContext : HttpStreamConnectionContext, IAsyncDisposable
{
    private static readonly byte[] ClientPreface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

    private readonly HPackDecoder _headerDecoder;
    private readonly Dictionary<int, Http2Stream> _streams;
    // RFC 9113 §6.5 — local settings are what we (the server) advertised
    // to the peer; remote settings are what the peer advertised to us.
    // Inbound frame parsing is bounded by the local cap; outbound frame
    // writing is bounded by the remote cap.
    private readonly Http2ConnectionSettings _localSettings;
    private readonly Http2ConnectionSettings _remoteSettings;
    // RFC 9113 §4.1 + §6.8 — a single TCP connection multiplexes many
    // logical streams. Frames from concurrent senders MUST NOT interleave
    // on the wire, or the peer's parser will see a corrupted frame
    // sequence. This semaphore serializes every outbound frame write on
    // the connection, regardless of whether it originated from a
    // SendAsync, a SETTINGS/PING ACK, a GOAWAY, or graceful shutdown.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int? _continuationStreamId;
    private bool _initialized;
    private bool _receivedClientSettings;
    private int _lastInboundStreamId;
    private int _gracefulCloseStarted;

    public Http2ConnectionContext(ITransportConnectionContext transportContext, bool isSecure)
        : base(transportContext, isSecure)
    {
        _headerDecoder = new HPackDecoder();
        _streams = new Dictionary<int, Http2Stream>();
        _localSettings = BuildLocalSettings();
        _remoteSettings = new Http2ConnectionSettings();
    }

    public override async IAsyncEnumerable<IHttpContext> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            ReceivedFrame? receivedFrame;
            try
            {
                receivedFrame = await ReadFrameAsync(Stream, _localSettings.MaxFrameSize, cancellationToken).ConfigureAwait(false);
            }
            catch (Http2ConnectionException error)
            {
                await EmitGoAwayAsync(error.ErrorCode, cancellationToken).ConfigureAwait(false);
                throw;
            }

            if (receivedFrame is null)
            {
                yield break;
            }

            Http2Context? completedContext;
            try
            {
                completedContext = await ProcessFrameAsync(receivedFrame.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (Http2ConnectionException error)
            {
                // RFC 9113 §6.8 — every connection-level failure MUST be
                // signalled to the peer with a GOAWAY carrying the offending
                // error code, before the underlying transport is torn down.
                await EmitGoAwayAsync(error.ErrorCode, cancellationToken).ConfigureAwait(false);
                throw;
            }

            if (completedContext is not null)
            {
                yield return completedContext;
            }
        }
    }

    public override async ValueTask SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        if (context is not Http2Context http2Context)
        {
            throw new InvalidOperationException("The supplied context does not belong to an HTTP/2 connection.");
        }

        byte[] bodyBytes = await ReadBodyAsync(http2Context.Response.Body, cancellationToken).ConfigureAwait(false);
        byte[] headerBlock = HPackEncoder.EncodeResponseHeaders(http2Context.Response.StatusCode, http2Context.Response.Headers, http2Context.Response.Cookies, bodyBytes.Length);

        // RFC 9113 §4.1 — frames from concurrent senders MUST NOT interleave.
        // The whole HEADERS [+ CONTINUATION...] [+ DATA...] sequence MUST be
        // emitted contiguously on the wire. Hold the connection write lock
        // for the duration so concurrent SendAsync calls and receive-loop
        // ACK frames cannot tear the frame sequence.
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteHeaderBlockAsync(http2Context.StreamId, headerBlock, endStream: bodyBytes.Length == 0, cancellationToken).ConfigureAwait(false);
            await WriteBodyAsync(http2Context.StreamId, bodyBytes, cancellationToken).ConfigureAwait(false);
            // RFC 9113 §6.8 / #686 — the caller MUST observe a guarantee that
            // the response bytes have been handed off to the transport pipe
            // before SendAsync returns. Flush the pipe writer here so the
            // bytes are durably committed to the send-side of the underlying
            // transport before the lock is released.
            await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        // RFC 9113 §3.4 — the connection preface MUST appear before any
        // other client data. A mismatch is a connection error with code
        // PROTOCOL_ERROR.
        byte[] preface = await ReadExactOrThrowAsync(Stream, ClientPreface.Length, cancellationToken).ConfigureAwait(false);

        if (!preface.AsSpan().SequenceEqual(ClientPreface))
        {
            await EmitGoAwayAsync(Http2ErrorCode.ProtocolError, cancellationToken).ConfigureAwait(false);
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                "The HTTP/2 connection preface did not match the expected sequence.");
        }

        // RFC 9113 §3.4 + §6.5 — immediately after sending or receiving
        // the preface, both peers send a SETTINGS frame (possibly empty)
        // as the first connection frame. We advertise the parameters
        // built from our local defaults instead of an empty payload so
        // the peer sees what we actually want.
        byte[] settingsPayload = EncodeLocalSettings();
        Http2Frame settingsFrame = new();
        settingsFrame.PrepareSettings(Http2SettingsFrameFlags.None);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Http2FrameWriter.WriteAsync(Stream, settingsFrame, settingsPayload, cancellationToken).ConfigureAwait(false);
            await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        _initialized = true;
    }

    private async Task<Http2Context?> ProcessFrameAsync(ReceivedFrame receivedFrame, CancellationToken cancellationToken)
    {
        // RFC 9113 §3.4 — the first frame the client sends after the
        // preface MUST be SETTINGS. Anything else is a PROTOCOL_ERROR.
        if (!_receivedClientSettings && receivedFrame.Frame.Type != Http2FrameType.Settings)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                $"Expected the HTTP/2 client SETTINGS frame as the first frame after the preface, but received '{receivedFrame.Frame.Type}'.");
        }

        if (_continuationStreamId.HasValue && receivedFrame.Frame.Type != Http2FrameType.Continuation)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                "Expected an HTTP/2 CONTINUATION frame before receiving another frame type.");
        }

        switch (receivedFrame.Frame.Type)
        {
            case Http2FrameType.Settings:
                await ProcessSettingsFrameAsync(receivedFrame, cancellationToken).ConfigureAwait(false);
                return null;
            case Http2FrameType.Headers:
                return ProcessHeadersFrame(receivedFrame);
            case Http2FrameType.Continuation:
                return ProcessContinuationFrame(receivedFrame);
            case Http2FrameType.Data:
                return ProcessDataFrame(receivedFrame);
            case Http2FrameType.Ping:
                await ProcessPingFrameAsync(receivedFrame, cancellationToken).ConfigureAwait(false);
                return null;
            case Http2FrameType.RstStream:
                _streams.Remove(receivedFrame.Frame.StreamId);
                if (_continuationStreamId == receivedFrame.Frame.StreamId)
                {
                    _continuationStreamId = null;
                }
                return null;
            default:
                return null;
        }
    }

    private async Task ProcessSettingsFrameAsync(ReceivedFrame receivedFrame, CancellationToken cancellationToken)
    {
        // RFC 9113 §6.5.1 — SETTINGS frames MUST be sent on stream 0.
        if (receivedFrame.Frame.StreamId != 0)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                $"SETTINGS frame received on stream {receivedFrame.Frame.StreamId}; SETTINGS frames MUST use stream 0.");
        }

        if (receivedFrame.Frame.SettingsAck)
        {
            // RFC 9113 §6.5 — an ACK frame MUST have an empty payload. A
            // non-empty payload is a connection error with code
            // FRAME_SIZE_ERROR.
            if (receivedFrame.Payload.Length != 0)
            {
                throw new Http2ConnectionException(
                    Http2ErrorCode.FrameSizeError,
                    $"SETTINGS ACK frame must have an empty payload, but received {receivedFrame.Payload.Length} octets.");
            }

            return;
        }

        // RFC 9113 §6.5.1 — a non-ACK SETTINGS frame's payload must be a
        // multiple of 6 octets (each setting is 16-bit identifier + 32-bit
        // value). A non-multiple is a FRAME_SIZE_ERROR.
        if (receivedFrame.Payload.Length % Http2FrameReader.SettingSize != 0)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.FrameSizeError,
                $"SETTINGS frame payload of {receivedFrame.Payload.Length} octets is not a multiple of {Http2FrameReader.SettingSize}.");
        }

        // Validate every parameter before applying any. RFC 9113 §6.5.2
        // — an invalid value is a connection error with the per-parameter
        // code listed in the spec (PROTOCOL_ERROR or FLOW_CONTROL_ERROR).
        System.Collections.Generic.IList<Http2PeerSetting> settings =
            Http2FrameReader.ReadSettings(new ReadOnlySequence<byte>(receivedFrame.Payload));

        foreach (Http2PeerSetting setting in settings)
        {
            (Http2ErrorCode errorCode, string? message) = Http2ConnectionSettings.Validate(setting);
            if (errorCode != Http2ErrorCode.NoError)
            {
                throw new Http2ConnectionException(errorCode, message ?? $"Invalid HTTP/2 SETTINGS value for {setting.Parameter}.");
            }
        }

        foreach (Http2PeerSetting setting in settings)
        {
            _remoteSettings.Apply(setting);
        }

        _receivedClientSettings = true;

        Http2Frame acknowledgement = new();
        acknowledgement.PrepareSettings(Http2SettingsFrameFlags.Acknowledge);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Http2FrameWriter.WriteAsync(Stream, acknowledgement, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private Http2Context? ProcessHeadersFrame(ReceivedFrame receivedFrame)
    {
        if (!_receivedClientSettings)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                "The HTTP/2 client SETTINGS frame must be received before HEADERS.");
        }

        Http2Frame frame = receivedFrame.Frame;
        _lastInboundStreamId = Math.Max(_lastInboundStreamId, frame.StreamId);
        Http2Stream stream = GetOrCreateStream(frame.StreamId);
        stream.AppendHeaders(receivedFrame.Payload, frame.HeadersEndHeaders);

        if (frame.HeadersEndStream)
        {
            stream.CompleteInput();
        }

        if (!frame.HeadersEndHeaders)
        {
            _continuationStreamId = frame.StreamId;
            return null;
        }

        return TryCompleteStream(stream);
    }

    private Http2Context? ProcessContinuationFrame(ReceivedFrame receivedFrame)
    {
        if (_continuationStreamId is null || _continuationStreamId.Value != receivedFrame.Frame.StreamId)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                "The HTTP/2 CONTINUATION frame did not match the active header block stream.");
        }

        Http2Stream stream = GetOrCreateStream(receivedFrame.Frame.StreamId);
        stream.AppendHeaders(receivedFrame.Payload, receivedFrame.Frame.HeadersEndHeaders);

        if (receivedFrame.Frame.HeadersEndHeaders)
        {
            _continuationStreamId = null;
        }

        return TryCompleteStream(stream);
    }

    private Http2Context? ProcessDataFrame(ReceivedFrame receivedFrame)
    {
        if (!_streams.TryGetValue(receivedFrame.Frame.StreamId, out Http2Stream? stream))
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                "The HTTP/2 DATA frame was received for an unknown stream.");
        }

        stream.AppendBody(receivedFrame.Payload, receivedFrame.Frame.DataEndStream);
        return TryCompleteStream(stream);
    }

    private async Task ProcessPingFrameAsync(ReceivedFrame receivedFrame, CancellationToken cancellationToken)
    {
        if (receivedFrame.Frame.PingAck)
        {
            return;
        }

        Http2Frame acknowledgement = new();
        acknowledgement.PreparePing(Http2PingFrameFlags.Acknowledge);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Http2FrameWriter.WriteAsync(Stream, acknowledgement, receivedFrame.Payload, cancellationToken).ConfigureAwait(false);
            await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private Http2Context? TryCompleteStream(Http2Stream stream)
    {
        if (!stream.IsRequestReady)
        {
            return null;
        }

        _streams.Remove(stream.StreamId);
        return stream.CreateContext(_headerDecoder, ConnectionInfo, GetScheme(ConnectionInfo.IsSecure), CancellationToken.None);
    }

    private Http2Stream GetOrCreateStream(int streamId)
    {
        if (!_streams.TryGetValue(streamId, out Http2Stream? stream))
        {
            stream = new Http2Stream(streamId);
            _streams.Add(streamId, stream);
        }

        return stream;
    }

    private async Task WriteHeaderBlockAsync(int streamId, byte[] headerBlock, bool endStream, CancellationToken cancellationToken)
    {
        int offset = 0;
        bool firstFrame = true;

        do
        {
            int remaining = headerBlock.Length - offset;
            int chunkLength = remaining > 0 ? Math.Min((int)_remoteSettings.MaxFrameSize, remaining) : 0;
            Http2Frame frame = new();

            if (firstFrame)
            {
                Http2HeadersFrameFlags flags = remaining <= chunkLength ? Http2HeadersFrameFlags.EndHeaders : Http2HeadersFrameFlags.None;

                if (endStream)
                {
                    flags |= Http2HeadersFrameFlags.EndStream;
                }

                frame.PrepareHeaders(flags, streamId);
            }
            else
            {
                frame.Type = Http2FrameType.Continuation;
                frame.Flags = remaining <= chunkLength ? (byte)Http2HeadersFrameFlags.EndHeaders : (byte)0;
                frame.StreamId = streamId;
            }

            await Http2FrameWriter.WriteAsync(
                Stream,
                frame,
                chunkLength == 0 ? ReadOnlyMemory<byte>.Empty : headerBlock.AsMemory(offset, chunkLength),
                cancellationToken).ConfigureAwait(false);

            offset += chunkLength;
            firstFrame = false;
        }
        while (offset < headerBlock.Length || firstFrame);
    }

    private async Task WriteBodyAsync(int streamId, byte[] bodyBytes, CancellationToken cancellationToken)
    {
        int offset = 0;

        while (offset < bodyBytes.Length)
        {
            int chunkLength = Math.Min((int)_remoteSettings.MaxFrameSize, bodyBytes.Length - offset);
            Http2Frame frame = new();
            frame.PrepareData(streamId);

            if (offset + chunkLength >= bodyBytes.Length)
            {
                frame.DataFlags |= Http2DataFrameFlags.EndStream;
            }

            await Http2FrameWriter.WriteAsync(Stream, frame, bodyBytes.AsMemory(offset, chunkLength), cancellationToken).ConfigureAwait(false);
            offset += chunkLength;
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

    private static async Task<byte[]?> ReadExactOrNullAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        int offset = 0;

        while (offset < count)
        {
            int bytesRead = await stream.ReadAsync(buffer, offset, count - offset, cancellationToken).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                return offset == 0 ? null : throw new EndOfStreamException("The HTTP/2 stream ended before a complete frame header was received.");
            }

            offset += bytesRead;
        }

        return buffer;
    }

    private static async Task<byte[]> ReadExactOrThrowAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        byte[]? buffer = await ReadExactOrNullAsync(stream, count, cancellationToken).ConfigureAwait(false);

        if (buffer is null)
        {
            throw new EndOfStreamException("The HTTP/2 stream ended before the expected data was received.");
        }

        return buffer;
    }

    private static async Task<ReceivedFrame?> ReadFrameAsync(Stream stream, uint maxFrameSize, CancellationToken cancellationToken)
    {
        byte[]? header = await ReadExactOrNullAsync(stream, Http2FrameReader.HeaderLength, cancellationToken).ConfigureAwait(false);

        if (header is null)
        {
            return null;
        }

        int payloadLength = (header[0] << 16) | (header[1] << 8) | header[2];

        // RFC 9113 §4.2 — a frame whose length exceeds the receiver's
        // advertised MAX_FRAME_SIZE is a connection error with code
        // FRAME_SIZE_ERROR.
        if (payloadLength > maxFrameSize)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.FrameSizeError,
                $"HTTP/2 frame payload length {payloadLength} exceeded the advertised maximum {maxFrameSize}.");
        }

        byte[] buffer = new byte[Http2FrameReader.HeaderLength + payloadLength];
        Buffer.BlockCopy(header, 0, buffer, 0, header.Length);

        if (payloadLength > 0)
        {
            byte[] payload = await ReadExactOrThrowAsync(stream, payloadLength, cancellationToken).ConfigureAwait(false);
            Buffer.BlockCopy(payload, 0, buffer, Http2FrameReader.HeaderLength, payload.Length);
        }

        ReadOnlySequence<byte> sequence = new(buffer);
        Http2Frame frame = new();

        if (!Http2FrameReader.TryReadFrame(ref sequence, frame, maxFrameSize, out ReadOnlySequence<byte> payloadSequence))
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.ProtocolError,
                "The HTTP/2 frame could not be parsed from the buffered payload.");
        }

        return new ReceivedFrame(frame, payloadSequence.IsEmpty ? Array.Empty<byte>() : payloadSequence.ToArray());
    }

    /// <summary>
    /// Emits a <c>GOAWAY</c> frame on stream 0 carrying
    /// <paramref name="errorCode"/> and the highest stream ID we have
    /// observed so far (RFC 9113 §6.8). The receive loop then propagates
    /// the exception so the listener can tear the connection down.
    /// </summary>
    private async Task EmitGoAwayAsync(Http2ErrorCode errorCode, CancellationToken cancellationToken)
    {
        try
        {
            Http2Frame goAway = new();
            goAway.PrepareGoAway(_lastInboundStreamId, errorCode);
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Http2FrameWriter.WriteAsync(Stream, goAway, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
                await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            // Best-effort: if the wire is already dead we can't deliver
            // the GOAWAY. The connection error will still surface to the
            // listener through the original exception.
        }
    }

    /// <summary>
    /// Builds the explicit server-side initial <see cref="Http2ConnectionSettings"/>
    /// we advertise to peers. The defaults mostly track the RFC 9113
    /// initial values except for ENABLE_PUSH (0 — Cohesion does not
    /// implement server push) and MAX_HEADER_LIST_SIZE (a 16 KB DoS
    /// guard instead of "unlimited").
    /// </summary>
    private static Http2ConnectionSettings BuildLocalSettings()
    {
        return new Http2ConnectionSettings
        {
            HeaderTableSize = Http2ConnectionSettings.InitialHeaderTableSize,
            EnablePush = 0,
            MaxConcurrentStreams = 100,
            InitialWindowSize = Http2ConnectionSettings.InitialInitialWindowSize,
            MaxFrameSize = Http2ConnectionSettings.InitialMaxFrameSize,
            MaxHeaderListSize = 16_384,
            EnableConnectProtocol = 0,
        };
    }

    /// <summary>
    /// Serialises <see cref="_localSettings"/> as a SETTINGS-frame
    /// payload (6 octets per parameter; identifier first, then value;
    /// both big-endian per RFC 9113 §6.5.1).
    /// </summary>
    private byte[] EncodeLocalSettings()
    {
        // Six parameters that change from the spec's initial values OR are
        // explicit choices we want the peer to see. We emit all of them
        // every time so the peer never has to guess our intent.
        (Http2SettingsParameter Parameter, uint Value)[] entries =
        {
            (Http2SettingsParameter.SETTINGS_HEADER_TABLE_SIZE, _localSettings.HeaderTableSize),
            (Http2SettingsParameter.SETTINGS_ENABLE_PUSH, _localSettings.EnablePush),
            (Http2SettingsParameter.SETTINGS_MAX_CONCURRENT_STREAMS, _localSettings.MaxConcurrentStreams),
            (Http2SettingsParameter.SETTINGS_INITIAL_WINDOW_SIZE, _localSettings.InitialWindowSize),
            (Http2SettingsParameter.SETTINGS_MAX_FRAME_SIZE, _localSettings.MaxFrameSize),
            (Http2SettingsParameter.SETTINGS_MAX_HEADER_LIST_SIZE, _localSettings.MaxHeaderListSize),
        };

        byte[] payload = new byte[entries.Length * Http2FrameReader.SettingSize];
        Span<byte> span = payload;

        for (int index = 0; index < entries.Length; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)entries[index].Parameter);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(span.Slice(2), entries[index].Value);
            span = span.Slice(Http2FrameReader.SettingSize);
        }

        return payload;
    }

    /// <summary>
    /// Performs an RFC 9113 §6.8 graceful close: emits a <c>GOAWAY</c>
    /// frame carrying <see cref="Http2ErrorCode.NoError"/>, then signals
    /// the transport's send pipeline that no further bytes will be
    /// written. The transport's send task reads the remaining bytes,
    /// performs its final <c>Socket.SendAsync</c> (which waits for the
    /// kernel to accept the bytes), then exits — at which point the
    /// underlying socket is torn down through the transport's own
    /// teardown path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Closes the gap identified by #686: previously, <see cref="Http2Connection.DisposeAsync"/>
    /// invoked <c>ITransportConnection.DisposeAsync</c> directly, which
    /// aborts the socket immediately and can race the send task's
    /// in-flight <c>Socket.SendAsync</c>. Completing the pipe writer
    /// here lets the send task drain naturally; the caller then waits
    /// for the connection state to transition out of <c>Open</c> before
    /// declaring the close complete.
    /// </para>
    /// <para>
    /// Idempotent — repeated invocations after the first are no-ops.
    /// </para>
    /// </remarks>
    public async ValueTask GracefulCloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _gracefulCloseStarted, 1) == 1)
        {
            return;
        }

        // EnsureInitializedAsync may not have run if the receive loop was
        // never started. Skip GOAWAY in that case; the peer has not seen
        // any HTTP/2 traffic from us yet, so closing the underlying socket
        // is enough.
        if (!_initialized)
        {
            await CompleteOutputAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await EmitGoAwayAsync(Http2ErrorCode.NoError, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await CompleteOutputAsync().ConfigureAwait(false);
        }
    }

    private ValueTask CompleteOutputAsync()
    {
        try
        {
            // RFC 9113 §6.8 — completing the pipe writer signals the
            // transport's send loop that no further bytes will be queued.
            // The send loop processes its remaining backlog (one final
            // Socket.SendAsync, which waits for the kernel to ACK) and
            // then exits — closing the socket cleanly.
            return Pipe.Output.CompleteAsync();
        }
        catch
        {
            // The writer may already be completed if a teardown ran
            // concurrently. Idempotent.
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await GracefulCloseAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    private readonly struct ReceivedFrame
    {
        public ReceivedFrame(Http2Frame frame, byte[] payload)
        {
            Frame = frame;
            Payload = payload;
        }

        public Http2Frame Frame { get; }

        public byte[] Payload { get; }
    }
}
