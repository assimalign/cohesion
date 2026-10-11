using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

/// <summary>
/// An HTTP/3 client written frame by frame over the in-memory multiplexed driver, for tests that must send
/// what a real client never does — a body that contradicts its Content-Length, a malformed trailer section
/// — and see how the server ends the stream. Request field sections are literal field lines
/// (<see cref="QPackTestCodec"/>, linked from the Web.WebSockets tests), and a response head is decoded
/// from what the server's static-only encoder emits.
/// </summary>
internal sealed class Http3RawClient : IAsyncDisposable
{
    private readonly MultiplexedConnection _connection;

    private Http3RawClient(MultiplexedConnection connection)
    {
        _connection = connection;
    }

    /// <summary>Dials <paramref name="listener"/>.</summary>
    public static async Task<Http3RawClient> ConnectAsync(InMemoryMultiplexedConnectionListener listener, CancellationToken cancellationToken)
    {
        MultiplexedConnection connection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, cancellationToken).ConfigureAwait(false);
        return new Http3RawClient(connection);
    }

    /// <summary>
    /// Opens a request stream and writes the request head — <paramref name="method"/> for
    /// <paramref name="path"/> with the regular <paramref name="fields"/> — then each of
    /// <paramref name="frames"/>, all in one write. With <paramref name="endStream"/> the client's FIN goes
    /// out in the same write, so the server sees the stream end with what it carries.
    /// </summary>
    /// <returns>The client end of the request stream.</returns>
    public async Task<Connection> SendRequestAsync(
        string method,
        string path,
        (string Name, string Value)[] fields,
        byte[][] frames,
        bool endStream,
        CancellationToken cancellationToken)
    {
        Connection stream = await _connection.OpenStreamAsync(ConnectionDirection.Bidirectional, cancellationToken).ConfigureAwait(false);
        List<(string Name, string Value)> head = new()
        {
            (":method", method),
            (":scheme", "https"),
            (":authority", "localhost"),
            (":path", path),
        };
        head.AddRange(fields);

        stream.Output.Write(QPackTestCodec.EncodeHeadersFrame(head));

        foreach (byte[] frame in frames)
        {
            stream.Output.Write(frame);
        }

        if (endStream)
        {
            await stream.Output.CompleteAsync().ConfigureAwait(false);
        }
        else
        {
            await stream.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return stream;
    }

    /// <summary>A DATA frame carrying <paramref name="payload"/>.</summary>
    public static byte[] Data(byte[] payload) => QPackTestCodec.EncodeFrame(QPackTestCodec.DataFrame, payload);

    /// <summary>A HEADERS frame carrying <paramref name="fields"/>: a trailer section after the request's DATA.</summary>
    public static byte[] Trailers(params (string Name, string Value)[] fields) => QPackTestCodec.EncodeHeadersFrame(fields);

    /// <summary>
    /// Reads what the server writes on <paramref name="stream"/> until it ends that direction, and returns
    /// the <c>:status</c> of every response head on it, interim ones first, or the error code the server
    /// reset the stream with.
    /// </summary>
    /// <returns>
    /// The statuses in wire order and <see langword="null"/>; or no status and the reset's application
    /// error code when the server reset the stream.
    /// </returns>
    public static async Task<(IReadOnlyList<int> Statuses, long? ResetCode)> ReadResponseAsync(Connection stream, CancellationToken cancellationToken)
    {
        List<byte> received = new();

        try
        {
            while (true)
            {
                ReadResult result = await stream.Input.ReadAsync(cancellationToken).ConfigureAwait(false);

                foreach (ReadOnlyMemory<byte> segment in result.Buffer)
                {
                    received.AddRange(segment.ToArray());
                }

                stream.Input.AdvanceTo(result.Buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (ConnectionResetException reset)
        {
            return (Array.Empty<int>(), reset.ApplicationErrorCode);
        }

        byte[] octets = received.ToArray();
        List<int> statuses = new();
        int index = 0;

        while (index < octets.Length)
        {
            QPackTestCodec.TryReadVariableLengthInteger(octets, ref index, out long type);
            QPackTestCodec.TryReadVariableLengthInteger(octets, ref index, out long length);
            ReadOnlySpan<byte> payload = octets.AsSpan(index, (int)length);
            index += (int)length;

            if (type == QPackTestCodec.HeadersFrame)
            {
                foreach ((string name, string value) in QPackTestCodec.DecodeFieldSection(payload))
                {
                    if (name == ":status")
                    {
                        statuses.Add(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }
        }

        return (statuses, null);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return _connection.DisposeAsync();
    }
}
