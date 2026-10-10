using System;

using Assimalign.Cohesion.Web.Cors.Internal;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Cors;

/// <summary>
/// Endpoint metadata that selects the CORS policy for a route: a named policy
/// (<see cref="CorsOptions.AddPolicy(string, CorsPolicy)"/>), an inline <see cref="CorsPolicy"/>, or
/// <see cref="Disabled"/> to keep CORS out of the endpoint's responses entirely.
/// </summary>
/// <remarks>
/// <para>
/// <c>UseCors</c> reads this metadata from the endpoint <c>UseRouting</c> publishes, last-wins
/// (<c>IRouterRouteMetadataCollection.GetMetadata&lt;CorsMetadata&gt;</c>), so a route's declaration
/// overrides its group's. An endpoint without it gets the default policy. For a CORS preflight the
/// metadata comes from the preflight's candidate endpoint, the one the actual request will reach.
/// </para>
/// <para>
/// The metadata implements <see cref="IRouteMiddlewareMetadata"/>, in every form including
/// <see cref="Disabled"/>: an endpoint that declares CORS fails with an <see cref="InvalidOperationException"/>
/// when it is dispatched without <c>UseCors</c> having processed it. A missing <c>UseCors</c> would only
/// make the browser block the endpoint's cross-origin readers, which is safe. A <c>UseCors</c> registered
/// ahead of <c>UseRouting</c> is not: it sees no endpoint and applies the default policy instead, to an
/// endpoint that declared a stricter policy or opted out. Failing at dispatch catches both. See the package
/// DESIGN.md.
/// </para>
/// <para>
/// This sealed carrier <em>is</em> the metadata contract. Metadata items in the endpoint bag are immutable
/// data carriers, and the sealed type guarantees the validated policy reference the middleware reads.
/// </para>
/// </remarks>
public sealed class CorsMetadata : IRouteMiddlewareMetadata
{
    private CorsMetadata(string? policyName, CorsPolicy? policy, bool isDisabled)
    {
        PolicyName = policyName;
        Policy = policy;
        IsDisabled = isDisabled;
    }

    /// <summary>
    /// Creates metadata that selects a named policy, resolved at request time against the policies
    /// registered with <see cref="CorsOptions.AddPolicy(string, CorsPolicy)"/>.
    /// </summary>
    /// <param name="policyName">The registered policy name (compared with ordinal, case-sensitive semantics).</param>
    /// <exception cref="ArgumentException"><paramref name="policyName"/> is <see langword="null"/> or empty.</exception>
    public CorsMetadata(string policyName)
        : this(ValidateName(policyName), policy: null, isDisabled: false)
    {
    }

    /// <summary>
    /// Creates metadata that applies an inline policy, without registering it by name.
    /// </summary>
    /// <param name="policy">The policy applied to requests matching the route.</param>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    public CorsMetadata(CorsPolicy policy)
        : this(policyName: null, ValidatePolicy(policy), isDisabled: false)
    {
    }

    /// <summary>
    /// Shared metadata that disables CORS for the endpoint it is attached to, overriding a broader policy
    /// and the default policy: its responses carry no CORS headers, and its preflights are left unanswered
    /// (the pipeline answers them as the plain <c>OPTIONS</c> requests they are), so a browser blocks
    /// cross-origin callers.
    /// </summary>
    public static CorsMetadata Disabled { get; } = new(policyName: null, policy: null, isDisabled: true);

    /// <summary>
    /// Gets the registered policy name to resolve at request time, or <see langword="null"/> when the
    /// metadata carries an inline <see cref="Policy"/> or is <see cref="Disabled"/>.
    /// </summary>
    public string? PolicyName { get; }

    /// <summary>
    /// Gets the inline policy, or <see langword="null"/> when the metadata carries a <see cref="PolicyName"/>
    /// or is <see cref="Disabled"/>.
    /// </summary>
    public CorsPolicy? Policy { get; }

    /// <summary>
    /// Gets whether the metadata disables CORS for the endpoint.
    /// </summary>
    public bool IsDisabled { get; }

    /// <summary>
    /// Gets the pipeline verb that must process this metadata before the endpoint runs: <c>UseCors</c>, in
    /// every form including <see cref="Disabled"/>.
    /// </summary>
    /// <remarks>
    /// Routing reads this when it dispatches the endpoint and fails the request with an
    /// <see cref="InvalidOperationException"/> when <c>UseCors</c> did not process it (see
    /// <see cref="IRouteMiddlewareMetadata"/>). Unlike a disabled rate limit, a disabled CORS policy places a
    /// requirement too: a <c>UseCors</c> that cannot see the endpoint would apply the default policy to it.
    /// </remarks>
    public string? RequiredMiddleware => CorsMiddleware.Verb;

    private static string ValidateName(string policyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyName);
        return policyName;
    }

    private static CorsPolicy ValidatePolicy(CorsPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy;
    }
}
