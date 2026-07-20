using System;

namespace Assimalign.Cohesion.Web.Caching;

/// <summary>
/// Endpoint metadata that attaches an output-caching decision to a route. Add an instance to a route's
/// metadata collection to cache that endpoint with a named policy
/// (<see cref="OutputCacheOptions.AddPolicy"/>) or an inline <see cref="OutputCachePolicy"/>, to opt an
/// endpoint in under the base/default policy with <see cref="Enabled"/>, or to opt it out entirely with
/// <see cref="Disabled"/>.
/// </summary>
/// <remarks>
/// <para>
/// The middleware resolves this metadata with last-wins semantics
/// (<c>IRouterRouteMetadataCollection.GetMetadata&lt;OutputCacheMetadata&gt;</c>), so an endpoint-level
/// declaration overrides a broader (for example group-level) one, and an endpoint's declaration overrides
/// the <see cref="OutputCacheOptions.BasePolicy"/>. <see cref="Disabled"/> suppresses caching for the
/// endpoint even when a base policy is configured.
/// </para>
/// <para>
/// This sealed carrier <em>is</em> the metadata contract — there is deliberately no
/// <c>IOutputCacheMetadata</c> interface. Metadata items in the endpoint bag are immutable data carriers,
/// and the sealed type guarantees the validated decision the middleware reads at the route-match seam.
/// </para>
/// </remarks>
public sealed class OutputCacheMetadata
{
    private OutputCacheMetadata(string? policyName, OutputCachePolicy? policy, bool isDisabled, bool isEnabledOptIn)
    {
        PolicyName = policyName;
        Policy = policy;
        IsDisabled = isDisabled;
        IsEnabledOptIn = isEnabledOptIn;
    }

    /// <summary>
    /// Creates metadata that caches the endpoint under a named policy, resolved at request time against
    /// the policies registered with <see cref="OutputCacheOptions.AddPolicy"/>.
    /// </summary>
    /// <param name="policyName">The registered policy name (compared with ordinal, case-sensitive semantics).</param>
    /// <exception cref="ArgumentException"><paramref name="policyName"/> is <see langword="null"/> or empty.</exception>
    public OutputCacheMetadata(string policyName)
        : this(ValidateName(policyName), policy: null, isDisabled: false, isEnabledOptIn: false)
    {
    }

    /// <summary>
    /// Creates metadata that caches the endpoint under an inline policy, without registering it by name.
    /// </summary>
    /// <param name="policy">The policy applied to responses from the endpoint.</param>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    public OutputCacheMetadata(OutputCachePolicy policy)
        : this(policyName: null, ValidatePolicy(policy), isDisabled: false, isEnabledOptIn: false)
    {
    }

    /// <summary>
    /// Shared metadata that opts the endpoint into caching under the <see cref="OutputCacheOptions.BasePolicy"/>
    /// (or a fresh default policy when none is configured). Use it to cache a specific endpoint while the
    /// middleware is otherwise opt-in.
    /// </summary>
    public static OutputCacheMetadata Enabled { get; } = new(policyName: null, policy: null, isDisabled: false, isEnabledOptIn: true);

    /// <summary>
    /// Shared metadata that disables output caching for the endpoint it is attached to, overriding any
    /// base or broader-scope policy.
    /// </summary>
    public static OutputCacheMetadata Disabled { get; } = new(policyName: null, policy: null, isDisabled: true, isEnabledOptIn: false);

    /// <summary>
    /// Gets the registered policy name to resolve at request time, or <see langword="null"/> when the
    /// metadata carries an inline <see cref="Policy"/>, is <see cref="Enabled"/>, or is <see cref="Disabled"/>.
    /// </summary>
    public string? PolicyName { get; }

    /// <summary>
    /// Gets the inline policy, or <see langword="null"/> when the metadata carries a <see cref="PolicyName"/>,
    /// is <see cref="Enabled"/>, or is <see cref="Disabled"/>.
    /// </summary>
    public OutputCachePolicy? Policy { get; }

    /// <summary>
    /// Gets whether the metadata disables caching for the endpoint.
    /// </summary>
    public bool IsDisabled { get; }

    /// <summary>
    /// Gets whether the metadata opts the endpoint into caching under the base/default policy.
    /// </summary>
    public bool IsEnabledOptIn { get; }

    private static string ValidateName(string policyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyName);
        return policyName;
    }

    private static OutputCachePolicy ValidatePolicy(OutputCachePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy;
    }
}
