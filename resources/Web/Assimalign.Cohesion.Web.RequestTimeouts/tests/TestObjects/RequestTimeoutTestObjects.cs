using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Routing;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.RequestTimeouts.Tests;

/// <summary>
/// Minimal <see cref="IWebApplicationPipelineBuilder"/> that composes middleware in registration
/// order — the same shape the real <c>WebApplication</c> builder produces — without pulling in
/// the hosting/DI stack.
/// </summary>
internal sealed class TestPipelineBuilder : IWebApplicationPipelineBuilder
{
    private readonly List<Func<WebApplicationMiddleware, WebApplicationMiddleware>> _middleware = new();

    public IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        _middleware.Add(middleware);
        return this;
    }

    public IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware)
        => Use(next => context => middleware.InvokeAsync(context, next));

    public IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware)
        => throw new NotSupportedException();

    public IWebApplicationPipeline Build()
    {
        WebApplicationMiddleware pipeline = _ => Task.CompletedTask;
        for (int i = _middleware.Count - 1; i >= 0; i--)
        {
            pipeline = _middleware[i].Invoke(pipeline);
        }

        return new TestPipeline(pipeline);
    }

    private sealed class TestPipeline : IWebApplicationPipeline
    {
        private readonly WebApplicationMiddleware _middleware;

        public TestPipeline(WebApplicationMiddleware middleware) => _middleware = middleware;

        public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
            => _middleware.Invoke(context);
    }
}

/// <summary>
/// A configurable <see cref="IHttpContext"/> test double whose request-cancellation token is
/// backed by a real source, with the transport cancel-request flag recorded the way
/// <c>TransportHttpContext</c> records it.
/// </summary>
internal sealed class TimeoutTestContext : IHttpContext
{
    private readonly CancellationTokenSource _requestAborted = new();

    public TimeoutTestContext(string path = "/", HttpMethod? method = null)
    {
        Request = new TestHttpRequest(this, path, method ?? HttpMethod.Get);
        Response = new TestHttpResponse(this);
    }

    public HttpVersion Version => HttpVersion.Http11;
    public IHttpRequest Request { get; }
    public IHttpResponse Response { get; }
    public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
    public CancellationToken RequestCancelled => _requestAborted.Token;

    /// <summary>Whether <see cref="Cancel"/>/<see cref="CancelAsync"/> was invoked (the transport reset path).</summary>
    public bool CancelRequested { get; private set; }

    public void Cancel()
    {
        CancelRequested = true;
        _requestAborted.Cancel();
    }

    public Task CancelAsync()
    {
        Cancel();
        return Task.CompletedTask;
    }

    /// <summary>Trips the underlying request token without the cancel-request flag — a client abort.</summary>
    public void AbortClient() => _requestAborted.Cancel();

    public ValueTask DisposeAsync()
    {
        _requestAborted.Dispose();
        return ValueTask.CompletedTask;
    }

    public string ReadResponseBody()
    {
        MemoryStream stream = (MemoryStream)Response.Body;
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

internal sealed class TestHttpRequest : IHttpRequest
{
    public TestHttpRequest(IHttpContext context, string path, HttpMethod method)
    {
        HttpContext = context;
        Path = new HttpPath(path);
        Method = method;
    }

    public HttpHost Host => HttpHost.Empty;
    public HttpPath Path { get; }
    public HttpMethod Method { get; }
    public HttpScheme Scheme => HttpScheme.Http;
    public IHttpQueryCollection Query { get; } = new HttpQueryCollection();
    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
    public IHttpContext HttpContext { get; }
    public Stream Body => Stream.Null;
}

internal sealed class TestHttpResponse : IHttpResponse
{
    public TestHttpResponse(IHttpContext context) => HttpContext = context;

    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
    public IHttpContext HttpContext { get; }
    public Stream Body { get; set; } = new MemoryStream();
}

/// <summary>
/// A stand-in for the router's route-match publication: installing it on the (decorated) feature
/// collection is exactly what <c>UseRouting</c> does between matching and dispatching.
/// </summary>
internal sealed class FakeRouteMatchFeature : IRouteMatchFeature
{
    private readonly IRouterRouteMetadataCollection _metadata;

    public FakeRouteMatchFeature(params object[] metadata)
        => _metadata = new Routing.Metadata.RouterRouteMetadataCollection(metadata);

    public string Name => nameof(IRouteMatchFeature);
    public IRouterRoute? Route => null;
    public RouteValueDictionary? Values => null;
    public IRouterRouteMetadataCollection Metadata => _metadata;
}

/// <summary>
/// A fake response-streaming feature reporting a started (head-committed) response, as the
/// streaming feature package would after the handler's first streamed write.
/// </summary>
internal sealed class FakeResponseStreamingFeature : IHttpResponseStreamingFeature
{
    public FakeResponseStreamingFeature(bool hasStarted) => HasStarted = hasStarted;

    public string Name => nameof(IHttpResponseStreamingFeature);
    public bool HasStarted { get; }
    public ValueTask StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask FlushAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask CompleteAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>
/// A hand-rolled <see cref="TimeProvider"/> whose timers fire only when the test advances the
/// clock — proving the middleware measures against the composed provider rather than wall time.
/// (The repo cannot use <c>Microsoft.Extensions.Time.Testing</c>.)
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _utcNow.UtcTicks;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state, dueTime);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>Advances the clock, firing every timer whose due time elapses.</summary>
    public void Advance(TimeSpan delta)
    {
        List<ManualTimer> due = new();

        lock (_gate)
        {
            _utcNow += delta;

            foreach (ManualTimer timer in _timers)
            {
                if (timer.AdvanceAndCheckDue(delta))
                {
                    due.Add(timer);
                }
            }
        }

        // Fire outside the gate: a callback cancels a CancellationTokenSource, which may run
        // continuations inline and re-enter the provider (e.g. to re-arm or dispose a timer).
        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan? _remaining;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            _remaining = Normalize(dueTime);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                _remaining = Normalize(dueTime);
            }

            return true;
        }

        public bool AdvanceAndCheckDue(TimeSpan delta)
        {
            if (_remaining is not { } remaining)
            {
                return false;
            }

            remaining -= delta;
            if (remaining <= TimeSpan.Zero)
            {
                _remaining = null;
                return true;
            }

            _remaining = remaining;
            return false;
        }

        public void Fire() => _callback.Invoke(_state);

        public void Dispose() => _owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private static TimeSpan? Normalize(TimeSpan dueTime)
            => dueTime == Timeout.InfiniteTimeSpan ? null : dueTime;
    }
}
