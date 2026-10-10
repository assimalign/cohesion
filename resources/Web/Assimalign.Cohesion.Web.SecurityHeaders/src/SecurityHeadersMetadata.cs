using System;

using Assimalign.Cohesion.Web.SecurityHeaders.Internal;

namespace Assimalign.Cohesion.Web.SecurityHeaders;

/// <summary>
/// Endpoint metadata that overrides the security headers for the responses of the route it is attached
/// to. It replaces the pipeline's policy with another one, adjusts a copy of the pipeline's policy, or
/// (<see cref="Disabled"/>) turns the headers off for the endpoint.
/// </summary>
/// <remarks>
/// <para>
/// The middleware reads this metadata, last-wins (<c>IRouterRouteMetadataCollection.GetMetadata&lt;SecurityHeadersMetadata&gt;</c>),
/// from the endpoint <c>UseRouting</c> published, at the moment it stages the headers: after the
/// downstream pipeline has run for a buffered response, or just before a streamed response commits its
/// head. The endpoint is known by then wherever <c>UseSecurityHeaders</c> sits, so the middleware can
/// stay at the front of the pipeline, where it also covers responses that never reach an endpoint
/// (static files, 404s, error pages), which always get the pipeline's own policy. A route-level
/// declaration overrides a group-level one. The candidate endpoint a CORS preflight publishes does not
/// run for the preflight, so its override does not apply to the preflight response.
/// </para>
/// <para>
/// Unlike rate-limiting or request-timeout metadata, this carrier does not implement
/// <c>IRouteMiddlewareMetadata</c>: that contract requires the middleware to acknowledge the endpoint
/// between <c>UseRouting</c> and dispatch, which a middleware placed ahead of routing, where this one
/// belongs, cannot do.
/// </para>
/// <para>
/// This sealed carrier is the metadata contract; there is deliberately no interface. A replacement
/// policy is copied and compiled when the metadata is created, so later changes to the instance passed in
/// do not reach the endpoint and an invalid value fails at map time.
/// </para>
/// </remarks>
public sealed class SecurityHeadersMetadata
{
    private readonly SecurityHeadersPlan? _replacement;
    private readonly Action<SecurityHeadersPolicy>? _adjustment;

    private SecurityHeadersMetadata(SecurityHeadersPlan? replacement, Action<SecurityHeadersPolicy>? adjustment, bool isDisabled)
    {
        _replacement = replacement;
        _adjustment = adjustment;
        IsDisabled = isDisabled;
    }

    /// <summary>
    /// Creates metadata that replaces the pipeline's policy with <paramref name="policy"/> for the endpoint.
    /// </summary>
    /// <param name="policy">The policy the endpoint's responses carry. A copy is captured.</param>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enumeration property of <paramref name="policy"/> holds an undefined value.</exception>
    public SecurityHeadersMetadata(SecurityHeadersPolicy policy)
        : this(SecurityHeadersPlan.Compile(new SecurityHeadersPolicy(ValidatePolicy(policy))), adjustment: null, isDisabled: false)
    {
    }

    /// <summary>
    /// Creates metadata that adjusts the pipeline's policy for the endpoint: the callback receives a copy
    /// of the policy configured on the <c>UseSecurityHeaders</c> that serves the request, and the endpoint's
    /// responses carry the result.
    /// </summary>
    /// <param name="configure">
    /// The adjustment, for example <c>policy =&gt; policy.Framing = FramingPolicy.SameOrigin</c>. It runs the
    /// first time a response for the endpoint is staged by each middleware, and its result is cached, so it
    /// must not depend on the request. It may run more than once; keep it free of side effects. An
    /// exception it throws surfaces on that first request.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    public SecurityHeadersMetadata(Action<SecurityHeadersPolicy> configure)
        : this(replacement: null, ValidateAdjustment(configure), isDisabled: false)
    {
    }

    /// <summary>
    /// Gets shared metadata that turns the security headers off for the endpoint: its responses carry none
    /// of the fields the middleware would emit. Fields the application sets itself are unaffected.
    /// </summary>
    public static SecurityHeadersMetadata Disabled { get; } = new(replacement: null, adjustment: null, isDisabled: true);

    /// <summary>
    /// Gets whether the metadata turns the security headers off for the endpoint.
    /// </summary>
    public bool IsDisabled { get; }

    /// <summary>
    /// Gets whether the metadata adjusts the pipeline's policy rather than replacing it.
    /// </summary>
    public bool IsAdjustment => _adjustment is not null;

    /// <summary>Gets the compiled replacement policy, or <see langword="null"/> for an adjustment or <see cref="Disabled"/>.</summary>
    internal SecurityHeadersPlan? Replacement => _replacement;

    /// <summary>Applies the adjustment to a copy of the pipeline's policy.</summary>
    internal SecurityHeadersPolicy Adjust(SecurityHeadersPolicy pipelinePolicy)
    {
        SecurityHeadersPolicy adjusted = new(pipelinePolicy);
        _adjustment!(adjusted);
        return adjusted;
    }

    private static SecurityHeadersPolicy ValidatePolicy(SecurityHeadersPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy;
    }

    private static Action<SecurityHeadersPolicy> ValidateAdjustment(Action<SecurityHeadersPolicy> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return configure;
    }
}
