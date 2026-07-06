using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web;

// Disambiguate from System.Net.HttpVersion, pulled in by the System.Net using for EndPoint.
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

    public HttpProtocol Protocols => HttpProtocol.Http11;

    public async Task<IHttpConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default)
    {
        return await _connections.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        _connections.Writer.TryComplete();
        return ValueTask.CompletedTask;
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
/// not, so a test can assert the server disposes the opened context directly.
/// </remarks>
internal sealed class FakeHttpConnectionContext : IHttpConnectionContext, IAsyncDisposable
{
    private readonly IReadOnlyList<IHttpContext> _exchanges;
    private readonly bool _parkAfterExchanges;
    private readonly Task? _holdUntil;

    private int _sendCount;
    private int _disposeCount;

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

    public EndPoint? LocalEndPoint => null;

    public EndPoint? RemoteEndPoint => null;

    public async IAsyncEnumerable<IHttpContext> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (IHttpContext exchange in _exchanges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return exchange;
        }

        if (_holdUntil is not null)
        {
            // Model an active connection holding its slot until the test releases it. WaitAsync
            // observes the shutdown token so a graceful stop still drains this connection.
            await _holdUntil.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (_parkAfterExchanges)
        {
            // Model an idle HTTP/1.1 keep-alive: block waiting for a next request that never comes,
            // until the server signals shutdown.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _sendCount);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A minimal <see cref="IHttpContext"/> exchange double. Only the members the server touches
/// (disposal) are implemented; the rest throw to prove the server never reads request/response
/// state during dispatch.
/// </summary>
internal sealed class FakeHttpContext : IHttpContext
{
    private int _disposeCount;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public HttpVersion Version => HttpVersion.Http11;

    public IHttpRequest Request => throw new NotSupportedException();

    public IHttpResponse Response => throw new NotSupportedException();

    public IHttpConnectionInfo ConnectionInfo => throw new NotSupportedException();

    public IHttpFeatureCollection Features => throw new NotSupportedException();

    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public CancellationToken RequestCancelled => CancellationToken.None;

    public void Cancel()
    {
    }

    public Task CancelAsync()
    {
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        return ValueTask.CompletedTask;
    }
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
