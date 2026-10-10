using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

/// <summary>
/// A minimal HTTP/3 client over the in-memory multiplexed driver, enough to open WebSockets with the
/// RFC 9220 extended CONNECT: the .NET <c>ClientWebSocket</c> does not speak HTTP/3. Each request
/// stream is returned as a <see cref="Http3RequestStream"/>, whose reads and writes carry the
/// tunnel's octets as <c>DATA</c> frames, so the BCL WebSocket runs over it unchanged.
/// </summary>
internal sealed class Http3TestClient : IAsyncDisposable
{
    private readonly MultiplexedConnection _connection;
    private readonly Connection _control;

    private Http3TestClient(MultiplexedConnection connection, Connection control)
    {
        _connection = connection;
        _control = control;
    }

    /// <summary>Connects to <paramref name="listener"/> and opens the client's control stream.</summary>
    public static async Task<Http3TestClient> ConnectAsync(InMemoryMultiplexedConnectionListener listener, CancellationToken cancellationToken)
    {
        MultiplexedConnection connection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, cancellationToken);

        // RFC 9114 §6.2.1: the control stream (type 0x00) opens with a SETTINGS frame (type 0x04),
        // here an empty one.
        Connection control = await connection.OpenStreamAsync(ConnectionDirection.WriteOnly, cancellationToken);
        await control.Output.WriteAsync(new byte[] { 0x00, 0x04, 0x00 }, cancellationToken);

        return new Http3TestClient(connection, control);
    }

    /// <summary>
    /// Sends an extended CONNECT for a WebSocket on a new request stream (RFC 9220 §3) and reads the
    /// response head.
    /// </summary>
    /// <param name="headers">The request's regular fields, such as <c>sec-websocket-version</c>.</param>
    /// <param name="cancellationToken">A token that cancels the exchange.</param>
    /// <param name="path">The request's <c>:path</c>.</param>
    /// <returns>The request stream, positioned after the response head, and the head's field lines.</returns>
    public async Task<(Http3RequestStream Stream, Http3ResponseHead Head)> ConnectWebSocketAsync(
        IEnumerable<(string Name, string Value)> headers,
        CancellationToken cancellationToken,
        string path = "/ws")
    {
        Connection connection = await _connection.OpenStreamAsync(ConnectionDirection.Bidirectional, cancellationToken);
        List<(string Name, string Value)> fields = new()
        {
            (":method", "CONNECT"),
            (":protocol", "websocket"),
            (":scheme", "https"),
            (":authority", "localhost"),
            (":path", path),
        };
        fields.AddRange(headers);

        await connection.Output.WriteAsync(QPackTestCodec.EncodeHeadersFrame(fields), cancellationToken);

        Http3RequestStream stream = new(connection);
        Http3ResponseHead head = new(await stream.ReadHeadersAsync(cancellationToken));
        return (stream, head);
    }

    public async ValueTask DisposeAsync()
    {
        await _control.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

/// <summary>
/// The field lines of an HTTP/3 response head.
/// </summary>
internal sealed class Http3ResponseHead
{
    public Http3ResponseHead(IReadOnlyList<(string Name, string Value)> fields)
    {
        Fields = fields;
    }

    public IReadOnlyList<(string Name, string Value)> Fields { get; }

    /// <summary>Gets the <c>:status</c> as a number.</summary>
    public int StatusCode => int.Parse(Field(":status") ?? throw new InvalidDataException("The response head has no :status."));

    /// <summary>Gets the first value of <paramref name="name"/>, or <see langword="null"/> when absent.</summary>
    public string? Field(string name)
    {
        foreach ((string fieldName, string value) in Fields)
        {
            if (string.Equals(fieldName, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }
}

/// <summary>
/// The client end of an HTTP/3 request stream as a duplex <see cref="Stream"/>: reads return the
/// payload of the server's <c>DATA</c> frames, skipping any other frame, and return 0 at the server's
/// FIN; writes send one <c>DATA</c> frame each; disposing sends the client's FIN.
/// </summary>
internal sealed class Http3RequestStream : Stream
{
    private readonly Connection _connection;
    private long _dataRemaining;
    private bool _disposed;

    public Http3RequestStream(Connection connection)
    {
        _connection = connection;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => !_disposed;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    private PipeReader Input => _connection.Input;

    /// <summary>
    /// Reads frames up to the first <c>HEADERS</c> frame and decodes its field section.
    /// </summary>
    public async Task<List<(string Name, string Value)>> ReadHeadersAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            (bool hasFrame, long type, long length) = await ReadFrameHeaderAsync(cancellationToken);

            if (!hasFrame)
            {
                throw new IOException("The server ended the stream before sending a response head.");
            }

            byte[] payload = await ReadPayloadAsync(length, cancellationToken);

            if (type == QPackTestCodec.HeadersFrame)
            {
                return QPackTestCodec.DecodeFieldSection(payload);
            }

            if (type == QPackTestCodec.DataFrame)
            {
                throw new IOException("The server sent DATA before the response head.");
            }
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_dataRemaining == 0)
        {
            (bool hasFrame, long type, long length) = await ReadFrameHeaderAsync(cancellationToken);

            if (!hasFrame)
            {
                return 0;
            }

            if (type == QPackTestCodec.DataFrame)
            {
                _dataRemaining = length;
            }
            else
            {
                // Trailers, reserved and unknown frame types carry nothing of the tunnel.
                await ReadPayloadAsync(length, cancellationToken);
            }
        }

        ReadResult result = await Input.ReadAsync(cancellationToken);
        ReadOnlySequence<byte> available = result.Buffer;

        if (available.IsEmpty && result.IsCompleted)
        {
            throw new IOException("The server ended the stream inside a DATA frame.");
        }

        int count = (int)Math.Min(Math.Min(available.Length, _dataRemaining), buffer.Length);
        available.Slice(0, count).CopyTo(buffer.Span);
        Input.AdvanceTo(available.GetPosition(count));
        _dataRemaining -= count;

        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] frame = QPackTestCodec.EncodeFrame(QPackTestCodec.DataFrame, buffer.Span);
        await _connection.Output.WriteAsync(frame, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _connection.Output.CompleteAsync();
        }

        await base.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _connection.Output.Complete();
        }

        base.Dispose(disposing);
    }

    private async ValueTask<(bool HasFrame, long Type, long Length)> ReadFrameHeaderAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await Input.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> available = result.Buffer;

            // A frame header is two variable-length integers, at most 16 octets.
            byte[] head = available.Slice(0, Math.Min(available.Length, 16)).ToArray();
            int index = 0;

            if (QPackTestCodec.TryReadVariableLengthInteger(head, ref index, out long type)
                && QPackTestCodec.TryReadVariableLengthInteger(head, ref index, out long length))
            {
                Input.AdvanceTo(available.GetPosition(index));
                return (true, type, length);
            }

            if (result.IsCompleted)
            {
                Input.AdvanceTo(available.End);

                if (available.IsEmpty)
                {
                    return (false, 0, 0);
                }

                throw new IOException("The server ended the stream inside a frame header.");
            }

            Input.AdvanceTo(available.Start, available.End);
        }
    }

    private async ValueTask<byte[]> ReadPayloadAsync(long length, CancellationToken cancellationToken)
    {
        byte[] payload = new byte[length];
        int filled = 0;

        while (filled < length)
        {
            ReadResult result = await Input.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> available = result.Buffer;

            if (available.IsEmpty && result.IsCompleted)
            {
                throw new IOException("The server ended the stream inside a frame.");
            }

            int count = (int)Math.Min(available.Length, length - filled);
            available.Slice(0, count).CopyTo(payload.AsSpan(filled));
            Input.AdvanceTo(available.GetPosition(count));
            filled += count;
        }

        return payload;
    }
}
