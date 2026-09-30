using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Metadata;
using Assimalign.Cohesion.Web.Routing.Patterns;

namespace Assimalign.Cohesion.Web.Routing.Tests.TestObjects;

/// <summary>
/// Wraps a <see cref="Route"/> and counts the routers built over it: <see cref="Router"/> reads
/// <see cref="InboundPrecedence"/> exactly once per route while it orders its candidates, and nothing
/// reads it at request time. An optional delay inside that read widens the window in which concurrent
/// first builds would overlap if building were not serialized.
/// </summary>
internal sealed class BuildCountingRoute : IRouterRoute
{
    private readonly Route _inner;
    private readonly TimeSpan _buildDelay;
    private int _buildCount;

    public BuildCountingRoute(Route inner, TimeSpan buildDelay = default)
    {
        _inner = inner;
        _buildDelay = buildDelay;
    }

    public int BuildCount => Volatile.Read(ref _buildCount);

    public IRouterRouteHandler Handler => _inner.Handler;

    public IReadOnlyCollection<HttpMethod> Methods => _inner.Methods;

    public RoutePattern? Pattern => _inner.Pattern;

    public decimal InboundPrecedence
    {
        get
        {
            Interlocked.Increment(ref _buildCount);

            if (_buildDelay > TimeSpan.Zero)
            {
                Thread.Sleep(_buildDelay);
            }

            return _inner.InboundPrecedence;
        }
    }

    public IRouterRouteMetadataCollection Metadata => _inner.Metadata;

    public bool TryMatchPath(IHttpContext context, out RouteValueDictionary values) => _inner.TryMatchPath(context, out values);

    public bool TryMatch(IHttpContext context, out RouteValueDictionary values) => _inner.TryMatch(context, out values);
}

/// <summary>
/// A thread-safe handler that counts its invocations and records the last cancellation token it
/// was handed.
/// </summary>
internal sealed class TokenRecordingRouterRouteHandler : IRouterRouteHandler
{
    private int _invocationCount;

    public int InvocationCount => Volatile.Read(ref _invocationCount);

    public CancellationToken LastToken { get; private set; }

    public Task InvokeAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _invocationCount);
        LastToken = cancellationToken;
        return Task.CompletedTask;
    }
}

/// <summary>
/// An <see cref="IRouterRoute"/> without a <see cref="RoutePattern"/>, standing in for a fully
/// custom matcher that is not addressable by outbound URL generation.
/// </summary>
internal sealed class PatternlessRoute : IRouterRoute
{
    public PatternlessRoute(IRouterRouteMetadataCollection? metadata = null)
    {
        Metadata = metadata ?? RouterRouteMetadataCollection.Empty;
    }

    public IRouterRouteHandler Handler { get; } = new RecordingRouterRouteHandler();

    public IReadOnlyCollection<HttpMethod> Methods { get; } = new List<HttpMethod>();

    public RoutePattern? Pattern => null;

    public decimal InboundPrecedence => 0m;

    public IRouterRouteMetadataCollection Metadata { get; }

    public bool TryMatchPath(IHttpContext context, out RouteValueDictionary values)
    {
        values = new RouteValueDictionary();
        return false;
    }

    public bool TryMatch(IHttpContext context, out RouteValueDictionary values)
    {
        values = new RouteValueDictionary();
        return false;
    }
}
