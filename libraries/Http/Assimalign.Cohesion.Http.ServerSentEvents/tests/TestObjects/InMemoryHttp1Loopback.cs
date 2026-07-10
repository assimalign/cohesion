using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Http.Connections;

namespace Assimalign.Cohesion.Http.ServerSentEvents.Tests.TestObjects;

/// <summary>
/// A minimal in-memory HTTP/1.1 loopback that composes the public
/// <see cref="Connections.InMemory"/> driver with the <see cref="HttpConnectionListener"/>
/// transport — deliberately using only public API so the Server-Sent Events package
/// can prove an end-to-end streamed response without depending on the transport's
/// internal test doubles.
/// </summary>
internal sealed class InMemoryHttp1Loopback : IAsyncDisposable
{
    private readonly Connection _client;
    private readonly HttpConnectionListener _listener;
    private IHttpConnection? _connection;
    private IHttpConnectionContext? _connectionContext;
    private IAsyncEnumerator<IHttpContext>? _enumerator;

    public InMemoryHttp1Loopback(string requestText)
    {
        (Connection client, Connection server) = InMemoryConnectionPair.Create(
            InMemoryConnectionPair.DefaultCapabilities,
            clientEndPoint: new IPEndPoint(IPAddress.Loopback, 8080),
            serverEndPoint: new IPEndPoint(IPAddress.Loopback, 5000));

        _client = client;

        // Preload the request onto the server's input (the client "sent" it, then finished).
        _client.Output.WriteAsync(Encoding.ASCII.GetBytes(requestText)).AsTask().GetAwaiter().GetResult();
        _client.Output.Complete();

        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new SingleConnectionListener(server));
        // Enable incremental response streaming by registering its response interceptor — the same
        // opt-in a real host performs. The transport itself has no streaming/SSE dependency.
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        _listener = new HttpConnectionListener(options);
    }

    /// <summary>Accepts the connection and returns the first request context.</summary>
    public async Task<IHttpContext> ReadRequestAsync()
    {
        _connection = await _listener.AcceptOrListenAsync().ConfigureAwait(false);
        _connectionContext = await _connection.OpenAsync().ConfigureAwait(false);
        _enumerator = _connectionContext.ReceiveAsync().GetAsyncEnumerator();

        if (!await _enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException("The in-memory HTTP/1.1 server produced no request context.");
        }

        return _enumerator.Current;
    }

    /// <summary>Reads whatever response bytes the server has flushed so far.</summary>
    public async Task<byte[]> ReadResponseAsync()
    {
        ReadResult result = await _client.Input.ReadAsync().ConfigureAwait(false);
        byte[] bytes = result.Buffer.ToArray();
        _client.Input.AdvanceTo(result.Buffer.End);
        return bytes;
    }

    /// <summary>Finalizes the response via the connection loop's send path.</summary>
    public ValueTask FinalizeResponseAsync(IHttpContext context)
        => _connectionContext!.SendAsync(context);

    public async ValueTask DisposeAsync()
    {
        if (_enumerator is not null)
        {
            await _enumerator.DisposeAsync().ConfigureAwait(false);
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        await _listener.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// A <see cref="ConnectionListener"/> that yields one pre-built connection then
    /// waits (never producing another) so the accept loop parks cleanly.
    /// </summary>
    private sealed class SingleConnectionListener : ConnectionListener
    {
        private Connection? _pending;

        public SingleConnectionListener(Connection connection)
        {
            _pending = connection;
        }

        public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 5000);

        public override ConnectionCapabilities Capabilities => InMemoryConnectionPair.DefaultCapabilities;

        public override async ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            Connection? connection = Interlocked.Exchange(ref _pending, null);
            if (connection is not null)
            {
                return connection;
            }

            // No more connections; park until cancelled so the HTTP accept loop idles.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
