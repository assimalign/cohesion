using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// A live HTTP/3 client/server pair over the in-memory multiplexed driver
/// (<see cref="InMemoryMultiplexedConnectionPair"/>). The server end is served by a real
/// <see cref="HttpConnectionListener"/> HTTP/3 registration; the test drives the client end — opening
/// request streams, writing frames at its own pace, and reading what the server writes back.
/// </summary>
/// <remarks>
/// Unlike the pre-filled <see cref="TestMultiplexedConnection"/>, streams stay open until the test ends
/// them and the server keeps accepting until the peer is disposed, so incremental body reads, concurrency
/// across streams, and the server stopping or resetting a stream are all observable. The in-memory
/// driver surfaces the reason the server resets a stream (or stops reading it) to the client verbatim,
/// which is how tests read the intended RFC 9114 §8.1 error code.
/// </remarks>
internal sealed class Http3InMemoryPeer : IAsyncDisposable
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private readonly MultiplexedConnection _client;
    private readonly MultiplexedConnection _server;
    private readonly HttpConnectionListener _listener;
    private readonly IAsyncEnumerator<IHttpContext> _receive;
    private bool _receiveStopped;

    private Http3InMemoryPeer(
        MultiplexedConnection client,
        MultiplexedConnection server,
        HttpConnectionListener listener,
        IHttpConnectionContext connectionContext)
    {
        _client = client;
        _server = server;
        _listener = listener;
        ConnectionContext = connectionContext;
        _receive = connectionContext.ReceiveAsync().GetAsyncEnumerator();
    }

    /// <summary>The server's HTTP/3 connection context.</summary>
    public IHttpConnectionContext ConnectionContext { get; }

    /// <summary>The server end of the multiplexed connection.</summary>
    public MultiplexedConnection Server => _server;

    /// <summary>
    /// Starts a server HTTP/3 registration over a fresh in-memory multiplexed connection pair.
    /// </summary>
    /// <param name="configure">Configures the HTTP/3 registration (limits, QPACK).</param>
    /// <param name="configureListener">Configures the listener options (interceptors).</param>
    /// <returns>The started peer.</returns>
    public static async Task<Http3InMemoryPeer> StartAsync(
        Action<Http3ConnectionListenerOptions>? configure = null,
        Action<HttpConnectionListenerOptions>? configureListener = null)
    {
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();

        HttpConnectionListenerOptions options = new();
        configureListener?.Invoke(options);
        options.UseHttp3(new TestMultiplexedConnectionListener(server), configure ?? (static _ => { }));

        HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        return new Http3InMemoryPeer(client, server, listener, connectionContext);
    }

    /// <summary>
    /// Opens a client-initiated bidirectional stream — a request stream, as the server sees it.
    /// </summary>
    /// <returns>The client end of the request stream.</returns>
    public async Task<Connection> OpenRequestStreamAsync()
    {
        return await _client.OpenStreamAsync(ConnectionDirection.Bidirectional);
    }

    /// <summary>
    /// Opens a client-initiated unidirectional stream — a control or QPACK stream, once the test writes
    /// its stream-type prefix.
    /// </summary>
    /// <returns>The client end of the unidirectional stream.</returns>
    public async Task<Connection> OpenUnidirectionalStreamAsync()
    {
        return await _client.OpenStreamAsync(ConnectionDirection.WriteOnly);
    }

    /// <summary>
    /// Advances the server's receive enumeration, failing the test instead of hanging when nothing
    /// arrives in time.
    /// </summary>
    /// <returns><see langword="true"/> when a context was yielded.</returns>
    public async Task<bool> MoveNextAsync()
    {
        return await _receive.MoveNextAsync().AsTask().WaitAsync(_timeout);
    }

    /// <summary>
    /// Waits for the next request context the server dispatches.
    /// </summary>
    /// <returns>The dispatched context.</returns>
    public async Task<IHttpContext> NextContextAsync()
    {
        if (!await MoveNextAsync())
        {
            throw new InvalidOperationException("The server's receive enumeration ended without dispatching a request.");
        }

        return _receive.Current;
    }

    /// <summary>
    /// Ends the server's receive enumeration (its teardown runs), leaving the connection itself open.
    /// </summary>
    /// <returns>A task that completes once the enumeration has been disposed.</returns>
    public async Task StopReceivingAsync()
    {
        if (!_receiveStopped)
        {
            _receiveStopped = true;
            await _receive.DisposeAsync();
        }
    }

    /// <summary>
    /// Reads everything the server writes on a request stream until it ends that direction (FIN). A
    /// stream the server reset surfaces the server's reason as the thrown exception.
    /// </summary>
    /// <param name="stream">The client end of a request stream.</param>
    /// <returns>The octets the server wrote.</returns>
    public static async Task<byte[]> ReadToEndAsync(Connection stream)
    {
        using CancellationTokenSource timeout = new(_timeout);
        List<byte> accumulated = new();

        while (true)
        {
            ReadResult result = await stream.Input.ReadAsync(timeout.Token);

            foreach (ReadOnlyMemory<byte> segment in result.Buffer)
            {
                accumulated.AddRange(segment.ToArray());
            }

            stream.Input.AdvanceTo(result.Buffer.End);

            if (result.IsCompleted)
            {
                return accumulated.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopReceivingAsync();
        await _listener.DisposeAsync();
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }
}
