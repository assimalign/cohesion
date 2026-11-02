using System;
using System.Collections.Frozen;
using System.Globalization;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Cors.Internal;

/// <summary>
/// The CORS middleware. It answers CORS preflights itself, without calling <c>next</c>, and stamps the
/// CORS headers on every other response its policy governs.
/// </summary>
/// <remarks>
/// <para>
/// The policy comes from the endpoint <c>UseRouting</c> published, so the middleware belongs after
/// <c>UseRouting</c>: the endpoint's last <see cref="CorsMetadata"/> selects a named or inline policy, or
/// disables CORS, and an endpoint without one (or a request with no endpoint) gets the default policy. With
/// no policy, the middleware steps aside and the request continues as though it were not registered.
/// </para>
/// <para>
/// A preflight's policy comes from its candidate endpoint, the one routing resolved for the requested
/// method, which never runs for the preflight. Two cases have no candidate. When no route serves the
/// requested method or path (routing's 405 or 404), the default policy answers, because the actual request
/// cannot reach a handler either. When routing selected an explicit route for the <c>OPTIONS</c> request
/// itself, that route is the policy source only if it also serves the requested method; an
/// <c>OPTIONS</c>-only route owns its path's preflights, and the middleware leaves them to it rather than
/// answer with a policy that may not be the actual endpoint's.
/// </para>
/// <para>
/// The middleware acknowledges every endpoint it processes (<see cref="Verb"/>), so an endpoint whose
/// <see cref="CorsMetadata"/> requires <c>UseCors</c> runs; registered ahead of <c>UseRouting</c> it sees no
/// endpoint, acknowledges none, and routing's dispatch then fails such an endpoint rather than run it under
/// the default policy.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("CorsMiddleware")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class CorsMiddleware : IWebApplicationMiddleware
{
    /// <summary>
    /// The pipeline verb the middleware acknowledges on the endpoint, and the one
    /// <see cref="CorsMetadata.RequiredMiddleware"/> names.
    /// </summary>
    internal const string Verb = "UseCors";

    private static readonly HttpHeaderKey[] _preservedHeaders =
    [
        HttpHeaderKey.AccessControlAllowOrigin,
        HttpHeaderKey.AccessControlAllowCredentials,
        HttpHeaderKey.AccessControlExposeHeaders,
        HttpHeaderKey.Vary,
    ];

    private readonly CorsSetting _setting;

    public CorsMiddleware(CorsSetting setting)
    {
        _setting = setting;
    }

    public Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        IRouteMatchFeature? match = context.GetRouteMatch();

        if (match is { IsPreflight: false })
        {
            // Every endpoint this middleware sees is acknowledged, with or without CORS metadata of its
            // own: routing checks the endpoint's last CorsMetadata item, which may be a group's, and a
            // same-origin request to an endpoint with a policy must run as well.
            context.AcknowledgeEndpointMiddleware(Verb);
        }

        if (CorsResponseHeaders.TryGetPreflightMethod(context.Request, out string requestedMethod))
        {
            return InvokePreflight(context, next, match, requestedMethod);
        }

        CorsPolicy? policy = ResolvePolicy(match is { IsPreflight: false } ? match.Metadata.GetMetadata<CorsMetadata>() : null);

        return policy is null ? next.Invoke(context) : InvokeActualAsync(context, next, policy);
    }

    private Task InvokePreflight(IHttpContext context, WebApplicationMiddleware next, IRouteMatchFeature? match, string requestedMethod)
    {
        CorsMetadata? metadata = null;

        if (match is not null)
        {
            if (!match.IsPreflight && match.Route is { } route && !Serves(route, requestedMethod))
            {
                // Routing selected an explicit route for the OPTIONS request itself, and the route does not
                // serve the requested method: the application owns OPTIONS on this path, preflights
                // included. Answering with this route's policy (or the default) could approve an actual
                // request whose own endpoint declares a stricter policy.
                CorsEventSource.Log.PreflightPassedThrough(context, "OptionsRouteOwnsPreflight", requestedMethod);
                return next.Invoke(context);
            }

            metadata = match.Metadata.GetMetadata<CorsMetadata>();
        }

        if (ResolvePolicy(metadata) is not { } policy)
        {
            // CORS is disabled for the endpoint, or no policy applies: the pipeline answers the preflight as
            // the plain OPTIONS request it is, and the browser fails it.
            CorsEventSource.Log.PreflightPassedThrough(context, metadata is { IsDisabled: true } ? "CorsDisabled" : "NoPolicy", requestedMethod);
            return next.Invoke(context);
        }

        CorsResponseHeaders.WritePreflight(context, policy, requestedMethod);
        return Task.CompletedTask;
    }

    private static Task InvokeActualAsync(IHttpContext context, WebApplicationMiddleware next, CorsPolicy policy)
    {
        IWebResponseStartingFeature starting = context.Features.Get<IWebResponseStartingFeature>()
            ?? throw new InvalidOperationException(
                ErrorMessages.CorsResponseStartingFeatureRequired);
        string? allowedOrigin = CorsResponseHeaders.ResolveAllowedOrigin(policy, context.Request);

        // Output caching reads Vary before transport commit. Publish the cache partition now, while
        // leaving the authoritative CORS grant and the final Vary merge to the starting callback.
        if (policy.VariesByOrigin)
        {
            CorsResponseHeaders.AppendVaryOrigin(context.Response.Headers);
        }

        // The callback outlives this middleware's turn, including a fault caught by an outer boundary.
        // The policy remains authoritative when a handler starts streaming or replaces buffered headers.
        // Preserve its final fields even when they already matched, including the early Vary declaration.
        starting.OnStarting(static state =>
        {
            var (exchange, selectedPolicy, origin) = ((IHttpContext, CorsPolicy, string?))state!;
            CorsResponseHeaders.WriteActual(exchange.Response.Headers, selectedPolicy, origin);
            return Task.CompletedTask;
        }, (context, policy, allowedOrigin), preservedHeaders: _preservedHeaders);

        return next.Invoke(context);
    }

    private CorsPolicy? ResolvePolicy(CorsMetadata? metadata)
    {
        if (metadata is null)
        {
            // Read on each request: an operator surface may replace the default policy at run time.
            return _setting.Value;
        }

        if (metadata.IsDisabled)
        {
            return null;
        }

        if (metadata.Policy is { } inline)
        {
            return inline;
        }

        if (_setting.Policies.TryGetValue(metadata.PolicyName!, out CorsPolicy? named))
        {
            return named;
        }

        throw new InvalidOperationException(
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.CorsPolicyNotRegistered, metadata.PolicyName));
    }

    // Whether the route routing matched for the OPTIONS request also serves the actual request's method, with
    // routing's own method semantics: byte for byte (RFC 9110 §9.1, so a route mapped for PATCH does not serve
    // the 'patch' a browser sends as written), an empty method set accepting any method, and GET serving HEAD.
    private static bool Serves(IRouterRoute route, string requestedMethod)
    {
        if (route.Methods.Count == 0)
        {
            return true;
        }

        foreach (HttpMethod method in route.Methods)
        {
            if (string.Equals(method.Value, requestedMethod, StringComparison.Ordinal)
                || (method == HttpMethod.Get && string.Equals(requestedMethod, HttpMethod.Head.Value, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class DebugView
    {
        public DebugView(CorsMiddleware value)
        {
        }
    }
}
