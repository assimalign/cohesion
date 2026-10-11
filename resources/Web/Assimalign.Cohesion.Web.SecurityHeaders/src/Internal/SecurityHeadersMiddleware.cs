using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Internal;

/// <summary>
/// The security-headers middleware. It stages the policy's fields exactly once per response, at the
/// last moment the head can still change, and resolves the policy at that moment, when the endpoint the
/// request was routed to (if any) is known.
/// </summary>
/// <remarks>
/// The server invokes the registered callback before either a buffered or streamed head commits. The
/// callback remains registered if an outer exception boundary replaces the response after this
/// middleware unwinds. Endpoint overrides and application-owned fields are resolved at that moment.
/// </remarks>
internal sealed class SecurityHeadersMiddleware : IWebApplicationMiddleware
{
    private static readonly HttpHeaderKey[] _preservedHeaders =
    [
        HttpHeaderKey.XContentTypeOptions,
        HttpHeaderKey.XFrameOptions,
        HttpHeaderKey.ContentSecurityPolicy,
        HttpHeaderKey.ContentSecurityPolicyReportOnly,
        HttpHeaderKey.ReferrerPolicy,
        HttpHeaderKey.CrossOriginOpenerPolicy,
        HttpHeaderKey.CrossOriginEmbedderPolicy,
        HttpHeaderKey.CrossOriginResourcePolicy,
        HttpHeaderKey.PermissionsPolicy,
    ];

    private readonly SecurityHeadersPolicy _policy;
    private readonly SecurityHeadersPlan _plan;
    private readonly ConcurrentDictionary<SecurityHeadersMetadata, SecurityHeadersPlan> _adjustedPlans = new();

    public SecurityHeadersMiddleware(SecurityHeadersPolicy policy)
    {
        _policy = policy;
        _plan = SecurityHeadersPlan.Compile(policy);
    }

    public Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        IWebResponseStartingFeature starting = context.Features.Get<IWebResponseStartingFeature>()
            ?? throw new InvalidOperationException(
                "UseSecurityHeaders requires IWebResponseStartingFeature. Use the default Web.Hosting server " +
                "or implement the response-starting feature in the custom server.");

        // One nonce per exchange: a nested registration, or an application-supplied feature, is reused.
        ISecurityHeadersFeature feature = context.Features.Get<ISecurityHeadersFeature>() ?? InstallFeature(context);
        // Preserve application-owned values kept by no-clobber, including both framing fields.
        starting.OnStarting(static state =>
        {
            var (middleware, exchange, nonce) = ((SecurityHeadersMiddleware, IHttpContext, ISecurityHeadersFeature))state!;
            middleware.Stage(exchange, nonce);
            return Task.CompletedTask;
        }, (this, context, feature), preservedHeaders: _preservedHeaders);

        return next.Invoke(context);
    }

    /// <summary>
    /// Stages the policy that applies to the exchange on its uncommitted response head.
    /// </summary>
    private void Stage(IHttpContext context, ISecurityHeadersFeature feature)
    {
        IHttpResponse response = context.Response;

        if (response.StatusCode == HttpStatusCode.NotModified || response.Headers.IsReadOnly)
        {
            return;
        }

        ResolvePlan(context)?.ApplyTo(response.Headers, feature);
    }

    /// <summary>
    /// Resolves the endpoint's override, when the request was routed to an endpoint that carries one, or
    /// the pipeline's policy. <see langword="null"/> when the endpoint disables the headers.
    /// </summary>
    private SecurityHeadersPlan? ResolvePlan(IHttpContext context)
    {
        if (context.GetRouteMatch() is not { IsPreflight: false } match
            || match.Metadata.GetMetadata<SecurityHeadersMetadata>() is not { } metadata)
        {
            return _plan;
        }

        if (metadata.IsDisabled)
        {
            return null;
        }

        if (metadata.Replacement is { } replacement)
        {
            return replacement;
        }

        // Adjustments derive from this middleware's own policy, so they are compiled per middleware and
        // cached by metadata instance; the route table bounds the cache.
        return _adjustedPlans.GetOrAdd(
            metadata,
            static (adjustment, policy) => SecurityHeadersPlan.Compile(adjustment.Adjust(policy)),
            _policy);
    }

    private static SecurityHeadersFeature InstallFeature(IHttpContext context)
    {
        SecurityHeadersFeature feature = new();
        context.Features.Set<ISecurityHeadersFeature>(feature);
        return feature;
    }
}
