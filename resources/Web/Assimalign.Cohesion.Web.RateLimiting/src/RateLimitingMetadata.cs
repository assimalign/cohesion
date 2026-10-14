using System;

using Assimalign.Cohesion.Web.RateLimiting.Internal;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.RateLimiting;

/// <summary>
/// Endpoint metadata that attaches a rate-limiting policy to a route. Add an instance to a route's
/// metadata collection to gate that endpoint with a named policy
/// (<see cref="RateLimitingOptions.AddPolicy"/>) or an inline <see cref="RateLimitingPolicy"/>, or
/// with <see cref="Disabled"/> to opt the endpoint out of per-endpoint gating entirely.
/// </summary>
/// <remarks>
/// <para>
/// The middleware reads this metadata from the endpoint <c>UseRouting</c> publishes, with last-wins
/// semantics (<c>IRouterRouteMetadataCollection.GetMetadata&lt;RateLimitingMetadata&gt;</c>), so an
/// endpoint-level declaration overrides a broader (for example group-level) one. A resolved policy is
/// evaluated <em>in addition to</em> the global limiter: both must grant a lease (see the package
/// DESIGN.md). <see cref="Disabled"/> removes only the per-endpoint gate; it does not exempt the endpoint
/// from the global limiter.
/// </para>
/// <para>
/// A policy is enforced only when <c>UseRateLimiting</c> runs between <c>UseRouting</c> and the endpoint.
/// The metadata therefore implements <see cref="IRouteMiddlewareMetadata"/>: an endpoint that names a
/// policy fails with an <see cref="InvalidOperationException"/> when it is dispatched without
/// <c>UseRateLimiting</c> having processed it (the middleware is missing, or registered ahead of
/// <c>UseRouting</c>), instead of running without its limit. <see cref="Disabled"/> places no such
/// requirement.
/// </para>
/// <para>
/// This sealed carrier <em>is</em> the metadata contract — there is deliberately no
/// <c>IRateLimitingMetadata</c> interface. Metadata items in the endpoint bag are immutable data
/// carriers, and the sealed type guarantees the validated policy reference the middleware reads from
/// the published endpoint.
/// </para>
/// </remarks>
public sealed class RateLimitingMetadata : IRouteMiddlewareMetadata
{
    private RateLimitingMetadata(string? policyName, RateLimitingPolicy? policy, bool isDisabled)
    {
        PolicyName = policyName;
        Policy = policy;
        IsDisabled = isDisabled;
    }

    /// <summary>
    /// Creates metadata that gates the endpoint with a named policy, resolved at request time against
    /// the policies registered with <see cref="RateLimitingOptions.AddPolicy"/>.
    /// </summary>
    /// <param name="policyName">The registered policy name (compared with ordinal, case-sensitive semantics).</param>
    /// <exception cref="ArgumentException"><paramref name="policyName"/> is <see langword="null"/> or empty.</exception>
    public RateLimitingMetadata(string policyName)
        : this(ValidateName(policyName), policy: null, isDisabled: false)
    {
    }

    /// <summary>
    /// Creates metadata that gates the endpoint with an inline policy, without registering it by name.
    /// </summary>
    /// <param name="policy">The policy applied to requests matching the route.</param>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    public RateLimitingMetadata(RateLimitingPolicy policy)
        : this(policyName: null, ValidatePolicy(policy), isDisabled: false)
    {
    }

    /// <summary>
    /// Shared metadata that disables the per-endpoint rate-limiting gate for the endpoint it is attached
    /// to, overriding any broader-scope policy. The global limiter still applies.
    /// </summary>
    public static RateLimitingMetadata Disabled { get; } = new(policyName: null, policy: null, isDisabled: true);

    /// <summary>
    /// Gets the registered policy name to resolve at request time, or <see langword="null"/> when the
    /// metadata carries an inline <see cref="Policy"/> or is <see cref="Disabled"/>.
    /// </summary>
    public string? PolicyName { get; }

    /// <summary>
    /// Gets the inline policy, or <see langword="null"/> when the metadata carries a <see cref="PolicyName"/>
    /// or is <see cref="Disabled"/>.
    /// </summary>
    public RateLimitingPolicy? Policy { get; }

    /// <summary>
    /// Gets whether the metadata disables the per-endpoint gate for the endpoint.
    /// </summary>
    public bool IsDisabled { get; }

    /// <summary>
    /// Gets the pipeline verb that must process this metadata before the endpoint runs:
    /// <c>UseRateLimiting</c> for a policy, or <see langword="null"/> for <see cref="Disabled"/>, which
    /// places no requirement.
    /// </summary>
    /// <remarks>
    /// Routing reads this when it dispatches the endpoint and fails the request with an
    /// <see cref="InvalidOperationException"/> when <c>UseRateLimiting</c> did not process it (see
    /// <see cref="IRouteMiddlewareMetadata"/>).
    /// </remarks>
    public string? RequiredMiddleware => IsDisabled ? null : RateLimitingMiddleware.Verb;

    private static string ValidateName(string policyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyName);
        return policyName;
    }

    private static RateLimitingPolicy ValidatePolicy(RateLimitingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy;
    }
}
