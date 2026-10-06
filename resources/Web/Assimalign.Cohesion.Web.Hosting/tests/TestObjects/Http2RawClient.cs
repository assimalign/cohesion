using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

/// <summary>
/// One HTTP/2 frame read off the wire (RFC 9113 §4.1).
/// </summary>
/// <param name="Type">The frame type octet.</param>
/// <param name="Flags">The frame flags octet.</param>
/// <param name="StreamId">The 31-bit stream identifier.</param>
/// <param name="Payload">The frame payload.</param>
internal readonly record struct Http2RawFrame(byte Type, byte Flags, int StreamId, byte[] Payload)
{
    public const byte DataType = 0x0;
    public const byte HeadersType = 0x1;
    public const byte RstStreamType = 0x3;
    public const byte SettingsType = 0x4;
    public const byte GoAwayType = 0x7;

    /// <summary>Whether a DATA or HEADERS frame carries END_STREAM (flag 0x1).</summary>
    public bool EndStream => Type is DataType or HeadersType && (Flags & 0x1) != 0;

    /// <summary>The last-stream-id of a GOAWAY frame.</summary>
    public int GoAwayLastStreamId => (int)(BinaryPrimitives.ReadUInt32BigEndian(Payload) & 0x7FFFFFFF);

    /// <summary>The error code of a GOAWAY frame.</summary>
    public uint GoAwayErrorCode => BinaryPrimitives.ReadUInt32BigEndian(Payload.AsSpan(4, 4));
}

/// <summary>
/// A prior-knowledge HTTP/2 client written frame by frame over the in-memory transport, for tests that
/// must see frames a real client consumes without surfacing — a server's <c>GOAWAY</c>, for one.
/// </summary>
/// <remarks>
/// Request headers go out as HPACK literal field lines without indexing and with literal names
/// (RFC 7541 §6.2.2), the one representation that needs no table state. The server's SETTINGS are
/// acknowledged as they arrive.
/// </remarks>
internal sealed class Http2RawClient : IAsyncDisposable
{
    private static readonly byte[] _preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

    private readonly Connection _connection;
    private readonly Stream _stream;
    private readonly List<Http2RawFrame> _frames = new();

    private Http2RawClient(Connection connection)
    {
        _connection = connection;
        _stream = connection.AsStream();
    }

    /// <summary>Every frame read so far, in wire order.</summary>
    public IReadOnlyList<Http2RawFrame> Frames => _frames;

    /// <summary>
    /// Dials <paramref name="transport"/> and sends the connection preface and an empty SETTINGS frame.
    /// </summary>
    public static async Task<Http2RawClient> ConnectAsync(InMemoryConnectionListener transport, CancellationToken cancellationToken)
    {
        Connection connection = await transport.CreateFactory().ConnectAsync(transport.EndPoint, cancellationToken).ConfigureAwait(false);
        Http2RawClient client = new(connection);

        await client._stream.WriteAsync(_preface, cancellationToken).ConfigureAwait(false);
        await client.WriteFrameAsync(Http2RawFrame.SettingsType, flags: 0, streamId: 0, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);

        return client;
    }

    /// <summary>
    /// Opens <paramref name="streamId"/> with a bodyless GET for <paramref name="path"/>.
    /// </summary>
    public Task SendGetAsync(int streamId, string path, CancellationToken cancellationToken)
    {
        using MemoryStream headerBlock = new();
        WriteLiteralField(headerBlock, ":method", "GET");
        WriteLiteralField(headerBlock, ":scheme", "http");
        WriteLiteralField(headerBlock, ":path", path);
        WriteLiteralField(headerBlock, ":authority", "localhost");

        // END_STREAM (0x1) | END_HEADERS (0x4).
        return WriteFrameAsync(Http2RawFrame.HeadersType, flags: 0x5, streamId, headerBlock.ToArray(), cancellationToken);
    }

    /// <summary>
    /// Reads frames until one satisfies <paramref name="predicate"/>, and returns that frame.
    /// </summary>
    public async Task<Http2RawFrame> ReadUntilAsync(Func<Http2RawFrame, bool> predicate, CancellationToken cancellationToken)
    {
        while (true)
        {
            byte[] header = await ReadExactAsync(9, cancellationToken).ConfigureAwait(false);
            int length = (header[0] << 16) | (header[1] << 8) | header[2];
            int streamId = (int)(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5, 4)) & 0x7FFFFFFF);
            Http2RawFrame frame = new(header[3], header[4], streamId, await ReadExactAsync(length, cancellationToken).ConfigureAwait(false));
            _frames.Add(frame);

            if (frame.Type == Http2RawFrame.SettingsType && (frame.Flags & 0x1) == 0)
            {
                // Acknowledge the server's SETTINGS (RFC 9113 §6.5.3).
                await WriteFrameAsync(Http2RawFrame.SettingsType, flags: 0x1, streamId: 0, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
            }

            if (predicate(frame))
            {
                return frame;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return _connection.DisposeAsync();
    }

    private async Task WriteFrameAsync(byte type, byte flags, int streamId, byte[] payload, CancellationToken cancellationToken)
    {
        byte[] header = new byte[9];
        header[0] = (byte)(payload.Length >> 16);
        header[1] = (byte)(payload.Length >> 8);
        header[2] = (byte)payload.Length;
        header[3] = type;
        header[4] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5), (uint)streamId);

        await _stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        await _stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    private static void WriteLiteralField(MemoryStream headerBlock, string name, string value)
    {
        // Literal field line without indexing, literal name: the 0x00 prefix, then each string as a
        // non-Huffman length (7-bit prefix; every value here is shorter than 127 octets) and its octets.
        headerBlock.WriteByte(0x00);
        WriteString(headerBlock, name);
        WriteString(headerBlock, value);
    }

    private static void WriteString(MemoryStream headerBlock, string value)
    {
        byte[] octets = Encoding.ASCII.GetBytes(value);
        headerBlock.WriteByte((byte)octets.Length);
        headerBlock.Write(octets, 0, octets.Length);
    }
}
