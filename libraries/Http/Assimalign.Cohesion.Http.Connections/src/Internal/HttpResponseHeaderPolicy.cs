using System;

namespace Assimalign.Cohesion.Http.Connections.Internal;

internal sealed class HttpResponseHeaderPolicy
{
    public static HttpResponseHeaderPolicy Default { get; } = new(HttpResponseDateCache.For(TimeProvider.System));
    public static HttpResponseHeaderPolicy Disabled { get; } = new(null);
    private readonly HttpResponseDateCache? _dateCache;

    public HttpResponseHeaderPolicy(HttpResponseDateCache? dateCache)
    {
        _dateCache = dateCache;
    }

    public HttpResponseDateHeader? GetDateHeader() => _dateCache?.GetCurrent();
}
