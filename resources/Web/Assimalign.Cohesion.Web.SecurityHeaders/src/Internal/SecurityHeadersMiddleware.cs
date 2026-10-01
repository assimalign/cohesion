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
/// <para>
/// The Web stack commits a response head in one of two places, and the middleware covers both:
/// </para>
/// <list type="bullet">
/// <item><b>Buffered (the default).</b> The server runs the whole pipeline, then sends the response, so
/// the head commits after this middleware returns. The fields are staged after <c>next</c> unwinds,
/// which also puts them after an exception boundary registered inside this middleware has reset the
/// headers and written its error response.</item>
/// <item><b>Streamed.</b> A handler that writes through <see cref="IHttpResponseStreamingFeature"/>
/// commits the head on its first start, write, flush or complete, before <c>next</c> returns. While
/// <c>next</c> runs, the middleware puts a decorator in that feature's place which stages the fields
/// immediately before the first such call.</item>
/// </list>
/// <para>
/// Nothing is staged ahead of <c>next</c>. That keeps "a field is present when the headers are staged"
/// equivalent to "something else set it", which is the whole no-clobber rule, and it leaves the endpoint
/// override to be resolved once, when the endpoint is known. A head committed some other way (by a
/// middleware ahead of this one, for instance) is left alone. A <c>304 Not Modified</c> gets no fields:
/// it updates a stored response that already carries the fields of the response that created it, and a
/// fresh nonce would no longer match the nonce attributes in that stored body.
/// </para>
/// </remarks>
internal sealed class SecurityHeadersMiddleware : IWebApplicationMiddleware
{
    private readonly SecurityHeadersPolicy _policy;
    private readonly SecurityHeadersPlan _plan;
    private readonly ConcurrentDictionary<SecurityHeadersMetadata, SecurityHeadersPlan> _adjustedPlans = new();

    public SecurityHeadersMiddleware(SecurityHeadersPolicy policy)
    {
        _policy = policy;
        _plan = SecurityHeadersPlan.Compile(policy);
    }

    public async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // One nonce per exchange: a nested registration, or an application-supplied feature, is reused.
        ISecurityHeadersFeature feature = context.Features.Get<ISecurityHeadersFeature>() ?? InstallFeature(context);

        SecurityHeadersStreamingFeature? streaming = null;
        if (context.Features.Get<IHttpResponseStreamingFeature>() is { HasStarted: false } transport)
        {
            streaming = new SecurityHeadersStreamingFeature(transport, this, context, feature);
            context.Features.Set<IHttpResponseStreamingFeature>(streaming);
        }

        try
        {
            await next.Invoke(context).ConfigureAwait(false);
        }
        finally
        {
            // The decorator lives exactly as long as this middleware's turn on the stack.
            if (streaming is not null)
            {
                context.Features.Set(streaming.Inner);
            }
        }

        // A streamed head already carried the fields; a head committed any other way can carry no more.
        if (streaming is { IsStaged: true } || context.Features.Get<IHttpResponseStreamingFeature>() is { HasStarted: true })
        {
            return;
        }

        Stage(context, feature);
    }

    /// <summary>
    /// Stages the policy that applies to the exchange on its uncommitted response head.
    /// </summary>
    internal void Stage(IHttpContext context, ISecurityHeadersFeature feature)
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
