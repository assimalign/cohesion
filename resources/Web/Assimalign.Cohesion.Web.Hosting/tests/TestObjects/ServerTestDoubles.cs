using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web;

// Disambiguate from System.Net.HttpVersion / HttpStatusCode, pulled in by the System.Net using for
// EndPoint.
using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using HttpVersion = Assimalign.Cohesion.Http.HttpVersion;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

/// <summary>
/// A listener double that hands out a fixed queue of connections in order, then parks
/// <see cref="AcceptOrListenAsync"/> until the accept token is cancelled — exactly how a real
/// listener idles while waiting for the next connection.
/// </summary>
internal sealed class FakeHttpConnectionListener : IHttpConnectionListener
{
    private readonly Channel<IHttpConnection> _connections = Channel.CreateUnbounded<IHttpConnection>();

    private int _disposeCount;
    private int _bindCount;
    private int _acceptCount;

    public FakeHttpConnectionListener(params IHttpConnection[] connections)
    {
        foreach (IHttpConnection connection in connections)
        {
            _connections.Writer.TryWrite(connection);
        }

        // Deliberately left open: once the seeded connections drain, ReadAsync blocks until the
        // accept token cancels, so the server's accept loop parks the same way it would in production.
    }

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public int BindCount => Volatile.Read(ref _bindCount);

    public int AcceptCount => Volatile.Read(ref _acceptCount);

    public Func<CancellationToken, ValueTask>? BindHandler { get; init; }

    public Func<ValueTask>? DisposeHandler { get; init; }

    /// <summary>
    /// Replaces the queued connections: every accept runs this instead, so a test can fault the
    /// accept loop.
    /// </summary>
    public Func<CancellationToken, Task<IHttpConnection>>? AcceptHandler { get; init; }

    public HttpProtocol Protocols => HttpProtocol.Http11;

    public ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _bindCount);
        return BindHandler?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
    }

    public async Task<IHttpConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _acceptCount);

        if (AcceptHandler is not null)
        {
            return await AcceptHandler(cancellationToken).ConfigureAwait(false);
        }

        return await _connections.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        _connections.Writer.TryComplete();

        if (DisposeHandler is not null)
        {
            await DisposeHandler().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// A connection double that records opens, aborts, and disposal, and signals <see cref="Disposed"/>
/// when its per-connection loop tears it down.
/// </summary>
internal sealed class FakeHttpConnection : IHttpConnection
{
    private readonly FakeHttpConnectionContext _context;

    private int _openCount;
    private int _disposeCount;
    private int _abortCount;

    public FakeHttpConnection(FakeHttpConnectionContext context)
    {
        _context = context;
    }

    public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeHttpConnectionContext Context => _context;

    public int OpenCount => Volatile.Read(ref _openCount);

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public int AbortCount => Volatile.Read(ref _abortCount);

    public Exception? AbortReason { get; private set; }

    public ConnectionId Id { get; } = ConnectionId.New();

    public ConnectionState State { get; private set; } = ConnectionState.Open;

    public CancellationToken ConnectionClosed => CancellationToken.None;

    public void Abort(Exception? reason = null)
    {
        Interlocked.Increment(ref _abortCount);
        AbortReason = reason;
        State = ConnectionState.Aborted;
    }

    public IHttpConnectionContext Open()
    {
        Interlocked.Increment(ref _openCount);
        return _context;
    }

    public ValueTask<IHttpConnectionContext> OpenAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _openCount);
        return new ValueTask<IHttpConnectionContext>(_context);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        State = ConnectionState.Closed;
        Disposed.TrySetResult();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A connection-context double whose receive sequence is scripted: it yields a fixed set of
/// exchanges, then optionally parks (modeling an idle keep-alive) or awaits a test-owned gate
/// (modeling an active-but-slow connection). Records sends and disposal.
/// </summary>
/// <remarks>
/// Implements <see cref="IAsyncDisposable"/> even though <see cref="IHttpConnectionContext"/> does
/// not, so a test can assert the server disposes the opened context directly. Like the real
/// transports, it yields the next exchange only when the server asks for it, so
/// <see cref="OnReceiving"/> observes exactly when the server's receive loop moved on.
/// </remarks>
internal sealed class FakeHttpConnectionContext : IHttpConnectionContext, IAsyncDisposable
{
    private readonly IReadOnlyList<IHttpContext> _exchanges;
    private readonly bool _parkAfterExchanges;
    private readonly Task? _holdUntil;
    private readonly TaskCompletionSource _gracefulClose = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _sendCount;
    private int _disposeCount;
    private int _gracefulCloseCount;

    public FakeHttpConnectionContext(
        IReadOnlyList<IHttpContext>? exchanges = null,
        bool parkAfterExchanges = false,
        Task? holdUntil = null)
    {
        _exchanges = exchanges ?? Array.Empty<IHttpContext>();
        _parkAfterExchanges = parkAfterExchanges;
        _holdUntil = holdUntil;
    }

    public int SendCount => Volatile.Read(ref _sendCount);

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>
    /// Gets how many times the server began the connection's graceful close.
    /// </summary>
    public int GracefulCloseCount => Volatile.Read(ref _gracefulCloseCount);

    /// <summary>
    /// Gets a task that completes when the server begins the connection's graceful close.
    /// </summary>
    public Task GracefulCloseRequested => _gracefulClose.Task;

    public Func<IHttpContext, CancellationToken, ValueTask>? SendHandler { get; init; }

    /// <summary>
    /// Invoked with each exchange immediately before it is yielded to the server.
    /// </summary>
    public Action<IHttpContext>? OnReceiving { get; init; }

    public EndPoint? LocalEndPoint { get; init; }

    public EndPoint? RemoteEndPoint { get; init; }

    public async IAsyncEnumerable<IHttpContext> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (IHttpContext exchange in _exchanges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnReceiving?.Invoke(exchange);
            yield return exchange;
        }

        if (_holdUntil is not null)
        {
            // Model an active connection holding its slot until the test releases it. WaitAsync
            // observes the receive token, so an aborted drain still unwinds this connection.
            await _holdUntil.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (_parkAfterExchanges)
        {
            // Model an idle keep-alive: wait for a next request that never comes, until the server
            // begins the connection's graceful close (which ends an idle connection's receive
            // sequence, as the real transports do) or aborts the drain.
            await _gracefulClose.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void BeginGracefulClose()
    {
        Interlocked.Increment(ref _gracefulCloseCount);
        _gracefulClose.TrySetResult();
    }

    public async ValueTask SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _sendCount);

        if (SendHandler is not null)
        {
            await SendHandler(context, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A minimal <see cref="IHttpContext"/> exchange double. Only the members the server touches are
/// implemented: the protocol version (which selects sequential or concurrent dispatch), the
/// cancellation surface, and disposal. <see cref="Response"/> exists only when a test supplies one —
/// the server reshapes a response solely to answer a faulted exchange with a 500 — and the rest
/// throw to prove the server never reads request state during dispatch.
/// </summary>
internal sealed class FakeHttpContext : IHttpContext
{
    private readonly IHttpResponse? _response;

    private int _disposeCount;
    private int _cancelCount;

    public FakeHttpContext(
        HttpVersion version = HttpVersion.Http11,
        IHttpResponse? response = null,
        CancellationToken requestCancelled = default)
    {
        Version = version;
        _response = response;
        RequestCancelled = requestCancelled;
    }

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>
    /// Gets how many times the exchange was cancelled — the server's request to reset it.
    /// </summary>
    public int CancelCount => Volatile.Read(ref _cancelCount);

    /// <summary>
    /// Invoked as the exchange is disposed, before its disposal count is incremented.
    /// </summary>
    public Action? OnDisposing { get; init; }

    public HttpVersion Version { get; }

    public IHttpRequest Request => throw new NotSupportedException();

    public IHttpResponse Response => _response ?? throw new NotSupportedException();

    public IHttpConnectionInfo ConnectionInfo => throw new NotSupportedException();

    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();

    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public CancellationToken RequestCancelled { get; }

    public void Cancel()
    {
        Interlocked.Increment(ref _cancelCount);
    }

    public Task CancelAsync()
    {
        Interlocked.Increment(ref _cancelCount);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        OnDisposing?.Invoke();
        Interlocked.Increment(ref _disposeCount);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A buffered response double: a mutable status, header collection, and body, so a test can stage
/// a partial response and observe how the server reshapes it.
/// </summary>
internal sealed class FakeHttpResponse : IHttpResponse
{
    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;

    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

    public IHttpContext HttpContext => throw new NotSupportedException();

    public Stream Body { get; set; } = new MemoryStream();
}

/// <summary>
/// A pipeline double that records every executed exchange and runs an optional per-exchange hook so
/// a test can throw, block, or signal from inside the middleware pipeline.
/// </summary>
internal sealed class FakePipeline : IWebApplicationPipeline
{
    private readonly Func<IHttpContext, CancellationToken, Task>? _onExecute;

    public FakePipeline(Func<IHttpContext, CancellationToken, Task>? onExecute = null)
    {
        _onExecute = onExecute;
    }

    public ConcurrentQueue<IHttpContext> Executed { get; } = new();

    public async Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        Executed.Enqueue(context);

        if (_onExecute is not null)
        {
            await _onExecute(context, cancellationToken).ConfigureAwait(false);
        }
    }
}
