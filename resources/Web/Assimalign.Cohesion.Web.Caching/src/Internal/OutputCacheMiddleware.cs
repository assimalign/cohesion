using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Caching.Internal;

/// <summary>
/// The output-cache middleware. For a cacheable GET/HEAD request it resolves the effective policy
/// (base policy overridden by the matched endpoint's <see cref="OutputCacheMetadata"/>), computes the
/// cache key, and serves a stored response directly — without invoking the endpoint — adding an
/// <c>Age</c> header. On a miss it wraps the response body in a size-capped buffering tee, invokes the
/// endpoint, and stores a cacheable outcome keyed so that the response's own <c>Vary</c> header governs
/// which client may receive it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Endpoint resolution.</b> Cohesion's router matches and dispatches in one step, so there is no
/// pipeline slot between "route matched" and "handler runs" for an async concern that must skip the
/// handler on a hit. This middleware therefore performs the router's own side-effect-free
/// <see cref="IRouter.Match(IHttpContext)"/> ahead of <c>UseRouting</c> to discover the endpoint and read
/// its <see cref="OutputCacheMetadata"/> before deciding — the same metadata seam the reactive
/// route-match feature exposes, resolved proactively so the lookup can await the store and short-circuit
/// the pipeline. When routing is not registered the base policy alone governs.
/// </para>
/// <para>
/// <b>Ordering.</b> Register <c>UseOutputCache</c> ahead of <c>UseResponseCompression</c> and any
/// content-negotiated write, so the buffered body captures the fully-encoded bytes and the captured
/// <c>Vary</c> already carries <c>Accept-Encoding</c>/<c>Accept</c>. Because the variant key folds in the
/// stored <c>Vary</c>, a client that cannot accept a stored variant computes a different key and never
/// receives it.
/// </para>
/// </remarks>
internal sealed class OutputCacheMiddleware : IWebApplicationMiddleware
{
    private static readonly HashSet<string> NonCacheableHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Upgrade",
        "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization", "Age",
        // Never replay one client's cookie grant to another: CacheAuthenticated opts the RESPONSE into
        // shared storage, but the Set-Cookie field itself is always per-recipient and never stored.
        "Set-Cookie",
    };

    private readonly IOutputCacheStore _store;
    private readonly OutputCacheOptions _options;
    private readonly OutputCachePolicy _defaultPolicy = new();
    private readonly OutputCacheFeature _feature;
    private readonly TimeProvider _timeProvider;

    public OutputCacheMiddleware(IOutputCacheStore store, OutputCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        _store = store;
        _options = options;
        _feature = new OutputCacheFeature(store);
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
    }

    public async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // Always publish the eviction handle so a handler can purge tags even when this request itself
        // is not cached.
        context.Features.Set<IOutputCacheFeature>(_feature);

        // Only safe, cacheable-by-definition methods participate. QUERY (RFC 10008) is deliberately not
        // included — see the package DESIGN.md (no request-content key seam yet).
        HttpMethod method = context.Request.Method;
        if (method != HttpMethod.Get && method != HttpMethod.Head)
        {
            await next.Invoke(context).ConfigureAwait(false);
            return;
        }

        // Resolve the endpoint ahead of routing (pure, side-effect-free) and its per-endpoint policy.
        RouteValueDictionary? routeValues = null;
        OutputCacheMetadata? metadata = null;
        if (context.Features.Get<IRouterFeature>() is { } routerFeature)
        {
            RouteMatch match = routerFeature.Router.Match(context);
            if (match.Status == RouteMatchStatus.Matched && match.Route is { } route)
            {
                routeValues = match.Values;
                metadata = route.Metadata.GetMetadata<OutputCacheMetadata>();
            }
        }

        OutputCachePolicy? policy = ResolvePolicy(metadata);
        if (policy is null || !policy.Enabled)
        {
            await next.Invoke(context).ConfigureAwait(false);
            return;
        }

        // Request-side bypass: no-store/no-cache from the client, or an authenticated request the policy
        // does not opt into caching.
        if (RequestForbidsCache(context.Request) || (HasAuthorization(context.Request) && !policy.CacheAuthenticated))
        {
            await next.Invoke(context).ConfigureAwait(false);
            return;
        }

        string primaryKey = OutputCacheKeyBuilder.BuildPrimaryKey(context, policy, routeValues);

        OutputCacheEntry? hit = await LookupAsync(context, primaryKey).ConfigureAwait(false);
        if (hit is not null && !IsResponseStarted(context))
        {
            await ServeAsync(context, hit).ConfigureAwait(false);
            return;
        }

        // Miss: buffer the response while the endpoint runs, then store a cacheable outcome.
        long perEntryCap = policy.MaximumBodySize ?? _options.MaximumBodySize;
        Stream originalBody = context.Response.Body;
        OutputCacheBufferStream buffer = new(originalBody, perEntryCap);
        context.Response.Body = buffer;

        try
        {
            await next.Invoke(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        await TryStoreAsync(context, policy, primaryKey, buffer).ConfigureAwait(false);
    }

    private async ValueTask<OutputCacheEntry?> LookupAsync(IHttpContext context, string primaryKey)
    {
        OutputCacheEntry? entry = await _store.GetAsync(primaryKey, context.RequestCancelled).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        if (!entry.IsVaryMarker)
        {
            // A representation stored directly under the primary key (the response carried no Vary).
            return entry;
        }

        // A vary marker: resolve the variant the current request maps to.
        string variantKey = OutputCacheKeyBuilder.BuildVariantKey(primaryKey, context.Request, entry.VaryBy);
        OutputCacheEntry? variant = await _store.GetAsync(variantKey, context.RequestCancelled).ConfigureAwait(false);
        return variant is { IsVaryMarker: false } ? variant : null;
    }

    private async Task ServeAsync(IHttpContext context, OutputCacheEntry entry)
    {
        IHttpResponse response = context.Response;

        response.StatusCode = entry.StatusCode;

        // The served response is fully owned by the cache: replace any transport-staged headers with the
        // stored block, then stamp the current Age.
        response.Headers.Clear();
        for (int i = 0; i < entry.Headers.Count; i++)
        {
            OutputCacheHeader header = entry.Headers[i];
            response.Headers[new HttpHeaderKey(header.Name)] = ToHeaderValue(header.Values);
        }

        long ageSeconds = (long)Math.Floor((_timeProvider.GetUtcNow() - entry.CreatedAt).TotalSeconds);
        if (ageSeconds < 0)
        {
            ageSeconds = 0;
        }
        response.Headers[HttpHeaderKey.Age] = ageSeconds.ToString(CultureInfo.InvariantCulture);

        byte[] body = entry.Body!;
        if (body.Length > 0)
        {
            // The response head is not started (guarded before serving); the write targets the transport's
            // response buffer, so it must not observe a client-cancel or the served bytes would be dropped.
            await response.Body.WriteAsync(body, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async ValueTask TryStoreAsync(IHttpContext context, OutputCachePolicy policy, string primaryKey, OutputCacheBufferStream buffer)
    {
        if (buffer.IsCapExceeded || buffer.CapturedBytes is not { } body)
        {
            return;
        }

        if (!IsResponseCacheable(context, policy))
        {
            return;
        }

        (IReadOnlyList<string> varyNames, bool varyStar) = ParseResponseVary(context.Response.Headers);
        if (varyStar)
        {
            // Vary: * marks the response uncacheable (RFC 9111 §4.1).
            return;
        }

        TimeSpan ttl = ComputeTimeToLive(context.Response, policy);
        if (ttl <= TimeSpan.Zero)
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        IReadOnlyList<string> tags = SnapshotTags(policy);
        List<OutputCacheHeader> headers = CaptureHeaders(context.Response.Headers);

        OutputCacheEntry response = new(context.Response.StatusCode, headers, body, now, ttl, tags, varyNames);

        if (varyNames.Count == 0)
        {
            // No Vary: store the representation directly under the primary key (single-lookup hit path).
            await _store.SetAsync(primaryKey, response, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        // Vary present: publish a marker under the primary key and the representation under the variant key.
        OutputCacheEntry marker = new(context.Response.StatusCode, Array.Empty<OutputCacheHeader>(), body: null, now, ttl, tags, varyNames);
        string variantKey = OutputCacheKeyBuilder.BuildVariantKey(primaryKey, context.Request, varyNames);

        await _store.SetAsync(primaryKey, marker, CancellationToken.None).ConfigureAwait(false);
        await _store.SetAsync(variantKey, response, CancellationToken.None).ConfigureAwait(false);
    }

    private OutputCachePolicy? ResolvePolicy(OutputCacheMetadata? metadata)
    {
        if (metadata is not null)
        {
            if (metadata.IsDisabled)
            {
                return null;
            }

            if (metadata.Policy is { } inline)
            {
                return inline;
            }

            if (metadata.PolicyName is { } name)
            {
                if (_options.TryGetPolicy(name, out OutputCachePolicy? named) && named is not null)
                {
                    return named;
                }

                throw new InvalidOperationException(
                    $"No output cache policy named '{name}' has been registered. " +
                    "Register it with options.AddPolicy(name, configure) in UseOutputCache.");
            }

            if (metadata.IsEnabledOptIn)
            {
                return _options.BasePolicy ?? _defaultPolicy;
            }
        }

        return _options.BasePolicy;
    }

    private bool IsResponseCacheable(IHttpContext context, OutputCachePolicy policy)
    {
        IHttpResponse response = context.Response;

        // Conservative default: only a 200 OK is stored. Other 2xx and heuristically-cacheable statuses
        // are deliberately excluded (documented in DESIGN.md).
        if (response.StatusCode.Value != HttpStatusCode.Ok.Value)
        {
            return false;
        }

        // An authenticated response (Set-Cookie) is not shared across clients unless the policy opts in.
        if (!policy.CacheAuthenticated && response.Headers.ContainsKey(HttpHeaderKey.SetCookie))
        {
            return false;
        }

        if (policy.HonorResponseCacheControl
            && response.Headers.TryGetValue(HttpHeaderKey.CacheControl, out HttpHeaderValue cacheControlValue)
            && HttpCacheControl.TryParse(cacheControlValue.Value, out HttpCacheControl cacheControl))
        {
            // no-store / private forbid a shared cache from storing; no-cache would require revalidation
            // this cache does not perform, so it is treated conservatively as non-storable.
            if (cacheControl.NoStore || cacheControl.Private || cacheControl.NoCache)
            {
                return false;
            }
        }

        return true;
    }

    private TimeSpan ComputeTimeToLive(IHttpResponse response, OutputCachePolicy policy)
    {
        TimeSpan ttl = policy.Duration;

        if (!policy.HonorResponseCacheControl)
        {
            return ttl;
        }

        HttpCacheControl cacheControl = default;
        bool hasCacheControl = response.Headers.TryGetValue(HttpHeaderKey.CacheControl, out HttpHeaderValue cacheControlValue)
            && HttpCacheControl.TryParse(cacheControlValue.Value, out cacheControl);

        DateTimeOffset? expires = ParseDate(response.Headers, HttpHeaderKey.Expires);
        DateTimeOffset? date = ParseDate(response.Headers, HttpHeaderKey.Date);

        TimeSpan? lifetime = HttpFreshness.GetFreshnessLifetime(
            hasCacheControl ? cacheControl : default,
            expires,
            date,
            shared: true);

        // The origin can shorten the policy duration but never lengthen it.
        return lifetime is { } explicitLifetime && explicitLifetime < ttl ? explicitLifetime : ttl;
    }

    private static bool RequestForbidsCache(IHttpRequest request)
    {
        return request.Headers.TryGetValue(HttpHeaderKey.CacheControl, out HttpHeaderValue value)
            && HttpCacheControl.TryParse(value.Value, out HttpCacheControl cacheControl)
            && (cacheControl.NoStore || cacheControl.NoCache);
    }

    private static bool HasAuthorization(IHttpRequest request)
        => request.Headers.TryGetValue(HttpHeaderKey.Authorization, out HttpHeaderValue value) && !value.IsEmpty;

    private static bool IsResponseStarted(IHttpContext context)
        => context.Features.Get<IHttpResponseStreamingFeature>() is { HasStarted: true };

    private static (IReadOnlyList<string> Names, bool VaryStar) ParseResponseVary(IHttpHeaderCollection headers)
    {
        if (!headers.TryGetValue(HttpHeaderKey.Vary, out HttpHeaderValue value) || value.IsEmpty)
        {
            return (Array.Empty<string>(), false);
        }

        List<string> names = new();
        foreach (string segment in value.Value.Split(','))
        {
            string token = segment.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            if (token == "*")
            {
                return (Array.Empty<string>(), true);
            }

            names.Add(token);
        }

        return (names, false);
    }

    private static List<OutputCacheHeader> CaptureHeaders(IHttpHeaderCollection headers)
    {
        List<OutputCacheHeader> captured = new();
        foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> pair in headers)
        {
            if (NonCacheableHeaders.Contains(pair.Key.Value))
            {
                continue;
            }

            HttpHeaderValue value = pair.Value;
            string?[] values = new string?[value.Count];
            for (int i = 0; i < value.Count; i++)
            {
                values[i] = value[i];
            }

            captured.Add(new OutputCacheHeader(pair.Key.Value, values));
        }

        return captured;
    }

    private static HttpHeaderValue ToHeaderValue(IReadOnlyList<string?> values)
    {
        if (values.Count == 1)
        {
            return new HttpHeaderValue(values[0]);
        }

        string?[] array = new string?[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            array[i] = values[i];
        }

        return new HttpHeaderValue(array);
    }

    private static IReadOnlyList<string> SnapshotTags(OutputCachePolicy policy)
    {
        if (policy.Tags.Count == 0)
        {
            return Array.Empty<string>();
        }

        string[] tags = new string[policy.Tags.Count];
        policy.Tags.CopyTo(tags, 0);
        return tags;
    }

    private static DateTimeOffset? ParseDate(IHttpHeaderCollection headers, HttpHeaderKey key)
    {
        if (headers.TryGetValue(key, out HttpHeaderValue value)
            && !value.IsEmpty
            && DateTimeOffset.TryParse(value.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
        {
            return parsed;
        }

        return null;
    }
}
