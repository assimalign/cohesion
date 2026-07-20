using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.RateLimiting.Internal;

/// <summary>
/// The <see cref="IHttpContext"/> the rate-limiting middleware hands downstream: a pass-through
/// decorator whose <see cref="Features"/> is the <see cref="RateLimitingFeatureCollection"/> that
/// observes the router's route-match publication and applies the endpoint policy. Every other member
/// forwards to the inner context unchanged — unlike the request-timeout decorator, rate limiting does
/// not swap the cancellation token.
/// </summary>
internal sealed class RateLimitingHttpContext : IHttpContext
{
    private readonly IHttpContext _inner;
    private readonly RateLimitingFeatureCollection _features;

    public RateLimitingHttpContext(IHttpContext inner, RateLimitingFeature feature, RateLimitingOptions options)
    {
        _inner = inner;
        _features = new RateLimitingFeatureCollection(inner.Features, inner, feature, options);
    }

    public HttpVersion Version => _inner.Version;

    public IHttpRequest Request => _inner.Request;

    public IHttpResponse Response => _inner.Response;

    public IHttpConnectionInfo ConnectionInfo => _inner.ConnectionInfo;

    public IHttpFeatureCollection Features => _features;

    public IDictionary<string, object?> Items => _inner.Items;

    public CancellationToken RequestCancelled => _inner.RequestCancelled;

    public void Cancel() => _inner.Cancel();

    public Task CancelAsync() => _inner.CancelAsync();

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
