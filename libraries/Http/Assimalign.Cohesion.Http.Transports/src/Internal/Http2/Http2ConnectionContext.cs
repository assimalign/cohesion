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

internal sealed class Http2ConnectionContext : HttpStreamConnectionContext
{
    private static readonly byte[] ClientPreface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

    private readonly HPackDecoder _headerDecoder;
    private readonly Dictionary<int, Http2Stream> _streams;
    private int? _continuationStreamId;
    private bool _initialized;
    private bool _receivedClientSettings;
    private uint _maxFrameSize;

    public Http2ConnectionContext(ITransportConnectionContext transportContext, bool isSecure)
        : base(transportContext, isSecure)
    {
        _headerDecoder = new HPackDecoder();
        _streams = new Dictionary<int, Http2Stream>();
        _maxFrameSize = 16_384;
    }

    public override async IAsyncEnumerable<IHttpContext> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            ReceivedFrame? receivedFrame = await ReadFrameAsync(Stream, _maxFrameSize, cancellationToken).ConfigureAwait(false);

            if (receivedFrame is null)
            {
                yield break;
            }

            Http2Context? completedContext = await ProcessFrameAsync(receivedFrame.Value, cancellationToken).ConfigureAwait(false);

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

        await WriteHeaderBlockAsync(http2Context.StreamId, headerBlock, endStream: bodyBytes.Length == 0, cancellationToken).ConfigureAwait(false);
        await WriteBodyAsync(http2Context.StreamId, bodyBytes, cancellationToken).ConfigureAwait(false);
        await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        byte[] preface = await ReadExactOrThrowAsync(Stream, ClientPreface.Length, cancellationToken).ConfigureAwait(false);

        if (!preface.AsSpan().SequenceEqual(ClientPreface))
        {
            throw new InvalidDataException("The HTTP/2 connection preface is invalid.");
        }

        Http2Frame settingsFrame = new();
        settingsFrame.PrepareSettings(Http2SettingsFrameFlags.None);
        await Http2FrameWriter.WriteAsync(Stream, settingsFrame, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        _initialized = true;
    }

    private async Task<Http2Context?> ProcessFrameAsync(ReceivedFrame receivedFrame, CancellationToken cancellationToken)
    {
        if (_continuationStreamId.HasValue && receivedFrame.Frame.Type != Http2FrameType.Continuation)
        {
            throw new InvalidDataException("Expected an HTTP/2 CONTINUATION frame before receiving another frame type.");
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
        if (receivedFrame.Frame.StreamId != 0)
        {
            throw new InvalidDataException("The HTTP/2 SETTINGS frame must be sent on stream 0.");
        }

        if (receivedFrame.Frame.SettingsAck)
        {
            return;
        }

        foreach (Http2PeerSetting setting in Http2FrameReader.ReadSettings(new ReadOnlySequence<byte>(receivedFrame.Payload)))
        {
            if (setting.Parameter == Http2SettingsParameter.SETTINGS_MAX_FRAME_SIZE)
            {
                _maxFrameSize = setting.Value;
            }
        }

        _receivedClientSettings = true;

        Http2Frame acknowledgement = new();
        acknowledgement.PrepareSettings(Http2SettingsFrameFlags.Acknowledge);
        await Http2FrameWriter.WriteAsync(Stream, acknowledgement, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private Http2Context? ProcessHeadersFrame(ReceivedFrame receivedFrame)
    {
        if (!_receivedClientSettings)
        {
            throw new InvalidDataException("The HTTP/2 client SETTINGS frame must be received before HEADERS.");
        }

        Http2Frame frame = receivedFrame.Frame;
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
            throw new InvalidDataException("The HTTP/2 CONTINUATION frame did not match the active header block stream.");
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
            throw new InvalidDataException("The HTTP/2 DATA frame was received for an unknown stream.");
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
        await Http2FrameWriter.WriteAsync(Stream, acknowledgement, receivedFrame.Payload, cancellationToken).ConfigureAwait(false);
        await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
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
            int chunkLength = remaining > 0 ? Math.Min((int)_maxFrameSize, remaining) : 0;
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
            int chunkLength = Math.Min((int)_maxFrameSize, bodyBytes.Length - offset);
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
            throw new InvalidDataException("The HTTP/2 frame could not be parsed from the buffered payload.");
        }

        return new ReceivedFrame(frame, payloadSequence.IsEmpty ? Array.Empty<byte>() : payloadSequence.ToArray());
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
