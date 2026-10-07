using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// An interactive HTTP/2 client driving one live server connection over the in-memory driver: the
/// peer's write side stays open, so a test can react to what the server writes — credit
/// flow-control windows as DATA arrives, reset a stream mid-response, or open another stream after an
/// earlier one was rejected. Disposal closes the server connection gracefully.
/// </summary>
internal sealed class Http2TestPeer : IAsyncDisposable
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private readonly HttpConnectionListener _listener;
    private readonly IHttpConnection _connection;
    private readonly IAsyncEnumerator<IHttpContext> _contexts;
    private long _nextPing;

    private Http2TestPeer(
        TestConnection transport,
        HttpConnectionListener listener,
        IHttpConnection connection,
        IHttpConnectionContext connectionContext,
        CancellationToken receiveToken)
    {
        Transport = transport;
        _listener = listener;
        _connection = connection;
        ConnectionContext = connectionContext;
        _contexts = connectionContext.ReceiveAsync(receiveToken).GetAsyncEnumerator();
        Output = new Http2FrameCollector(transport);
    }

    /// <summary>The in-memory transport between the peer and the server.</summary>
    public TestConnection Transport { get; }

    /// <summary>The server's HTTP/2 connection context.</summary>
    public IHttpConnectionContext ConnectionContext { get; }

    /// <summary>The server's output, parsed into frames.</summary>
    public Http2FrameCollector Output { get; }

    /// <summary>
    /// Opens a server connection and sends the client preface plus the client's SETTINGS frame.
    /// </summary>
    /// <param name="options">The listener options (interceptors); a fresh instance when omitted.</param>
    /// <param name="configure">Configures the HTTP/2 registration (limits).</param>
    /// <param name="initialWindowSize">
    /// The client's <c>SETTINGS_INITIAL_WINDOW_SIZE</c> — the server's initial send window for every
    /// stream — or <see langword="null"/> to keep the RFC 9113 default of 65535.
    /// </param>
    /// <param name="decorate">
    /// Wraps the transport the server is handed (for example to observe or interfere with its
    /// writes), or <see langword="null"/> to serve the transport as is. The peer keeps reading and
    /// writing the undecorated transport.
    /// </param>
    /// <param name="receiveToken">
    /// The token the peer passes to <see cref="IHttpConnectionContext.ReceiveAsync"/>, standing in for
    /// the host's token for the connection; cancelling it is the host giving up on the connection.
    /// </param>
    /// <returns>The connected peer.</returns>
    public static async Task<Http2TestPeer> ConnectAsync(
        HttpConnectionListenerOptions? options = null,
        Action<Http2ConnectionListenerOptions>? configure = null,
        uint? initialWindowSize = null,
        Func<Connection, Connection>? decorate = null,
        CancellationToken receiveToken = default)
    {
        byte[] settings = initialWindowSize is { } windowSize
            ? Http2TestSettings.SettingsPayload((Http2TestSettings.Parameter.InitialWindowSize, windowSize))
            : Array.Empty<byte>();
        byte[] opening = Combine(
            Http2TestSettings.Preface(),
            Http2TestSettings.RawFrame(0x4, 0, 0, settings));

        TestConnection transport = new(opening, completeInput: false);
        Connection served = decorate?.Invoke(transport) ?? transport;
        options ??= new HttpConnectionListenerOptions();
        options.UseHttp2(new TestConnectionListener(served), configure ?? (_ => { }));

        HttpConnectionListener listener = new(options);
        IHttpConnection connection = await listener.AcceptOrListenAsync();
        IHttpConnectionContext connectionContext = await connection.OpenAsync();
        return new Http2TestPeer(transport, listener, connection, connectionContext, receiveToken);
    }

    /// <summary>The request fields of a plain <c>GET</c> to <paramref name="path"/>.</summary>
    public static (string Name, string Value)[] Get(string path) => Request("GET", path);

    /// <summary>The request fields of <paramref name="method"/> to <paramref name="path"/>, plus any extra fields.</summary>
    public static (string Name, string Value)[] Request(string method, string path, params (string Name, string Value)[] extra)
    {
        List<(string Name, string Value)> fields = new()
        {
            (":method", method),
            (":scheme", "https"),
            (":path", path),
            (":authority", "api.test"),
        };

        fields.AddRange(extra);
        return fields.ToArray();
    }

    /// <summary>A deterministic body of <paramref name="length"/> octets.</summary>
    public static byte[] CreateBody(int length)
    {
        byte[] body = new byte[length];

        for (int index = 0; index < body.Length; index++)
        {
            body[index] = (byte)(index % 251);
        }

        return body;
    }

    /// <summary>Waits for the server to dispatch its next request.</summary>
    public async Task<IHttpContext> ReceiveContextAsync()
    {
        bool dispatched = await _contexts.MoveNextAsync().AsTask().WaitAsync(_timeout);
        dispatched.ShouldBeTrue("the server should have dispatched another request");
        return _contexts.Current;
    }

    /// <summary>
    /// Waits for the server's receive enumeration to end — once the receive token is cancelled, its
    /// frame pump has stopped and aborted whatever it was going to abort — and asserts it dispatched
    /// nothing more.
    /// </summary>
    public async Task WaitForReceiveEndAsync()
    {
        bool dispatched;

        try
        {
            dispatched = await _contexts.MoveNextAsync().AsTask().WaitAsync(_timeout);
        }
        catch (OperationCanceledException)
        {
            dispatched = false;
        }

        dispatched.ShouldBeFalse("the server should have stopped receiving");
    }

    /// <summary>Sends a HEADERS frame (END_HEADERS, plus END_STREAM when requested).</summary>
    public Task SendHeadersAsync(int streamId, bool endStream, params (string Name, string Value)[] fields)
        => SendAsync(HttpProtocolPayloadFactory.CreateHttp2HeadersFrame(streamId, (byte)(endStream ? 0x4 | 0x1 : 0x4), fields));

    /// <summary>Sends one DATA frame.</summary>
    public Task SendDataAsync(int streamId, byte[] data, bool endStream)
        => SendAsync(Http2TestSettings.RawFrame(0x0, endStream ? (byte)0x1 : (byte)0x0, streamId, data));

    /// <summary>Sends a WINDOW_UPDATE crediting <paramref name="increment"/> octets (stream 0 = connection).</summary>
    public Task SendWindowUpdateAsync(int streamId, int increment)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)increment & 0x7FFFFFFF);
        return SendAsync(Http2TestSettings.RawFrame(0x8, 0, streamId, payload));
    }

    /// <summary>Sends an RST_STREAM carrying <paramref name="errorCode"/>.</summary>
    public Task SendRstStreamAsync(int streamId, Http2ErrorCode errorCode)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)errorCode);
        return SendAsync(Http2TestSettings.RawFrame(0x3, 0, streamId, payload));
    }

    /// <summary>
    /// A write barrier: sends a PING and waits for its acknowledgement. The server's pump writes the
    /// ACK through the same write gate as response frames, so every frame a response writer could
    /// emit without further input is on the wire (and collected) once the ACK arrives.
    /// </summary>
    public async Task SyncAsync()
    {
        byte[] opaque = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(opaque, ++_nextPing);
        await SendAsync(Http2TestSettings.RawFrame(0x6, 0, 0, opaque));
        await Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsPingAck && frame.Payload.AsSpan().SequenceEqual(opaque)),
            $"the acknowledgement of PING {_nextPing}");
    }

    /// <summary>Sends raw frame bytes to the server.</summary>
    public Task SendAsync(params byte[][] frames) => Transport.WriteInputAsync(Combine(frames));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _contexts.DisposeAsync();
        await _connection.DisposeAsync();
        await _listener.DisposeAsync();
    }

    private static byte[] Combine(params byte[][] buffers)
    {
        using MemoryStream stream = new();

        foreach (byte[] buffer in buffers)
        {
            stream.Write(buffer, 0, buffer.Length);
        }

        return stream.ToArray();
    }
}
