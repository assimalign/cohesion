using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

/// <summary>
/// A WebSocket client over a raw loopback socket: it writes the opening handshake and frames byte
/// by byte, so a test can send what a well-behaved client never would (an unmasked frame, a long
/// control frame, a reserved bit) and read exactly what the server answers.
/// </summary>
internal sealed class RawWebSocketClient : IAsyncDisposable
{
    /// <summary>The sample key of RFC 6455 §1.3.</summary>
    public const string SampleKey = "dGhlIHNhbXBsZSBub25jZQ==";

    private static readonly byte[] _mask = { 0x37, 0xFA, 0x21, 0x3D };

    private readonly Socket _socket;
    private readonly NetworkStream _stream;

    private RawWebSocketClient(Socket socket)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
    }

    public static async Task<RawWebSocketClient> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken);
        return new RawWebSocketClient(socket);
    }

    /// <summary>
    /// Writes an opening handshake and reads the response head. The defaults form a valid
    /// handshake; pass <see langword="null"/> to leave a field out.
    /// </summary>
    public async Task<RawHandshakeResponse> HandshakeAsync(
        CancellationToken cancellationToken,
        string method = "GET",
        string? key = SampleKey,
        string? version = "13",
        string? origin = null,
        string? extensions = null,
        string? protocols = null)
    {
        StringBuilder request = new();
        request.Append(method).Append(" /ws HTTP/1.1\r\n");
        request.Append("Host: 127.0.0.1\r\n");
        request.Append("Connection: Upgrade\r\n");
        request.Append("Upgrade: websocket\r\n");
        AppendField(request, "Sec-WebSocket-Key", key);
        AppendField(request, "Sec-WebSocket-Version", version);
        AppendField(request, "Origin", origin);
        AppendField(request, "Sec-WebSocket-Extensions", extensions);
        AppendField(request, "Sec-WebSocket-Protocol", protocols);
        request.Append("\r\n");

        await _stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), cancellationToken);
        await _stream.FlushAsync(cancellationToken);

        return await ReadResponseHeadAsync(cancellationToken);
    }

    /// <summary>
    /// Writes one frame: <paramref name="firstByte"/> carries FIN, RSV1-3 and the opcode; the payload
    /// is masked unless <paramref name="masked"/> is <see langword="false"/>.
    /// </summary>
    public async Task SendFrameAsync(byte firstByte, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken, bool masked = true)
    {
        List<byte> frame = new(payload.Length + 14) { firstByte };
        byte maskBit = masked ? (byte)0x80 : (byte)0x00;

        if (payload.Length < 126)
        {
            frame.Add((byte)(maskBit | payload.Length));
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            frame.Add((byte)(maskBit | 126));
            frame.Add((byte)(payload.Length >> 8));
            frame.Add((byte)payload.Length);
        }
        else
        {
            frame.Add((byte)(maskBit | 127));
            for (int shift = 56; shift >= 0; shift -= 8)
            {
                frame.Add((byte)((long)payload.Length >> shift));
            }
        }

        ReadOnlySpan<byte> bytes = payload.Span;
        if (masked)
        {
            frame.AddRange(_mask);
            for (int i = 0; i < bytes.Length; i++)
            {
                frame.Add((byte)(bytes[i] ^ _mask[i % 4]));
            }
        }
        else
        {
            frame.AddRange(bytes.ToArray());
        }

        await _stream.WriteAsync(frame.ToArray(), cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    /// <summary>Reads one frame the server sent (server frames are never masked).</summary>
    public async Task<RawFrame> ReadFrameAsync(CancellationToken cancellationToken)
    {
        byte[] header = new byte[2];
        await _stream.ReadExactlyAsync(header, cancellationToken);

        long length = header[1] & 0x7F;
        if (length == 126)
        {
            byte[] extended = new byte[2];
            await _stream.ReadExactlyAsync(extended, cancellationToken);
            length = (extended[0] << 8) | extended[1];
        }
        else if (length == 127)
        {
            byte[] extended = new byte[8];
            await _stream.ReadExactlyAsync(extended, cancellationToken);
            length = 0;
            foreach (byte value in extended)
            {
                length = (length << 8) | value;
            }
        }

        byte[] payload = new byte[length];
        await _stream.ReadExactlyAsync(payload, cancellationToken);

        return new RawFrame(header[0], header[1], payload);
    }

    /// <summary>
    /// Reads whatever the server sends until it closes the connection or <paramref name="window"/>
    /// passes; used to show that nothing else arrives.
    /// </summary>
    public async Task<int> ReadRemainingAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(window);
        byte[] buffer = new byte[1024];
        int total = 0;

        try
        {
            int read;
            while ((read = await _stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                total += read;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }

        return total;
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _socket.Dispose();
    }

    private async Task<RawHandshakeResponse> ReadResponseHeadAsync(CancellationToken cancellationToken)
    {
        // Byte by byte, so nothing after the head (the first frame) is consumed.
        List<byte> head = new();
        byte[] one = new byte[1];

        while (head.Count < 4 || head[^4] != '\r' || head[^3] != '\n' || head[^2] != '\r' || head[^1] != '\n')
        {
            await _stream.ReadExactlyAsync(one, cancellationToken);
            head.Add(one[0]);
        }

        string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        int statusCode = int.Parse(lines[0].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            string name = lines[i][..colon].Trim();
            string value = lines[i][(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out string? existing) ? existing + ", " + value : value;
        }

        return new RawHandshakeResponse(statusCode, headers);
    }

    private static void AppendField(StringBuilder request, string name, string? value)
    {
        if (value is not null)
        {
            request.Append(name).Append(": ").Append(value).Append("\r\n");
        }
    }
}
