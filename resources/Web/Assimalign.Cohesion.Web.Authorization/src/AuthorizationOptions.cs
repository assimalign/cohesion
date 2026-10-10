using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Authorization;

/// <summary>
/// Builder-time options for authorization: the default policy, the optional fallback policy, and the
/// named policies endpoints reference. Configure them in the
/// <c>builder.Services.AddAuthorization(...)</c> callback.
/// </summary>
/// <remarks>
/// <para>
/// Composition is dependency-free: policies are values captured at builder time, with no service
/// container, configuration binding, or request-time service location. When the
/// <c>AddAuthorization</c> callback returns, the options become read-only and every mutator throws
/// <see cref="InvalidOperationException"/>, so the policies <c>UseAuthorization</c> evaluates cannot
/// change underneath concurrent requests.
/// </para>
/// <para>
/// Policy names are compared ordinal (case-sensitive), as the sibling rate-limiting policy map does:
/// a name is a developer-chosen key, so an exact match is the least surprising rule, and a mistyped
/// name fails the request instead of resolving to a different policy.
/// </para>
/// <para>
/// The registered options can be read back from the application context with
/// <see cref="AuthorizationWebApplicationExtensions.TryGetAuthorizationOptions(IWebApplicationContext, out AuthorizationOptions)"/>.
/// A component that describes endpoints rather than serving them, such as an OpenAPI document
/// generator, resolves an endpoint's authorization through <see cref="GetEffectivePolicy"/>, the
/// computation <c>UseAuthorization</c> itself runs, so the description and the enforcement cannot
/// disagree.
/// </para>
/// </remarks>
public sealed class AuthorizationOptions
{
    private readonly Dictionary<string, AuthorizationPolicy> _policies = new(StringComparer.Ordinal);

    private AuthorizationPolicy _defaultPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    private AuthorizationPolicy? _fallbackPolicy;
    private bool _isReadOnly;

    /// <summary>
    /// Gets or sets the policy an endpoint gets when its authorization metadata names no policy and no
    /// roles, as <c>RequireAuthorization()</c> with no arguments does. Defaults to a policy that requires
    /// an authenticated user.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The options are read-only.</exception>
    public AuthorizationPolicy DefaultPolicy
    {
        get => _defaultPolicy;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            ThrowIfReadOnly();

            _defaultPolicy = value;
        }
    }

    /// <summary>
    /// Gets or sets the policy applied to every request that reaches <c>UseAuthorization</c> without
    /// authorization metadata: an endpoint that declares none, and a request no endpoint was selected
    /// for. <see langword="null"/> (the default) authorizes those requests without evaluation.
    /// </summary>
    /// <remarks>
    /// Set it to make authorization the default, for example to
    /// <see cref="DefaultPolicy"/>, and exempt the endpoints that must stay public (a login page) with
    /// <c>AllowAnonymous()</c>. Because it also covers requests no route matched, an anonymous caller
    /// is challenged instead of learning which paths exist. A CORS preflight is never authorized.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The options are read-only.</exception>
    public AuthorizationPolicy? FallbackPolicy
    {
        get => _fallbackPolicy;
        set
        {
            ThrowIfReadOnly();

            _fallbackPolicy = value;
        }
    }

    /// <summary>
    /// Registers a named policy that endpoints reference with <c>RequireAuthorization(policyName)</c>
    /// or <see cref="AuthorizationMetadata(string)"/>.
    /// </summary>
    /// <param name="name">The policy name, compared ordinal (case-sensitive). Must be unique.</param>
    /// <param name="policy">The policy.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A policy with the same name is already registered, or the options are read-only.
    /// </exception>
    public AuthorizationOptions AddPolicy(string name, AuthorizationPolicy policy)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(policy);
        ThrowIfReadOnly();

        if (!_policies.TryAdd(name, policy))
        {
            throw new InvalidOperationException($"An authorization policy named '{name}' is already registered.");
        }

        return this;
    }

    /// <summary>
    /// Builds and registers a named policy that endpoints reference with
    /// <c>RequireAuthorization(policyName)</c> or <see cref="AuthorizationMetadata(string)"/>.
    /// </summary>
    /// <param name="name">The policy name, compared ordinal (case-sensitive). Must be unique.</param>
    /// <param name="configure">Configures the policy's requirements and schemes.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The configured policy has no requirement, a policy with the same name is already registered, or
    /// the options are read-only.
    /// </exception>
    public AuthorizationOptions AddPolicy(string name, Action<AuthorizationPolicyBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(configure);
        ThrowIfReadOnly();

        AuthorizationPolicyBuilder builder = new();
        configure.Invoke(builder);

        return AddPolicy(name, builder.Build());
    }

    /// <summary>
    /// Resolves a policy registered with <see cref="AddPolicy(string, AuthorizationPolicy)"/>.
    /// </summary>
    /// <remarks>
    /// This is the lookup <c>RequireAuthorization(policyName)</c> resolves through. Reading is safe from
    /// any number of threads once <c>AddAuthorization</c> has made the options read-only.
    /// </remarks>
    /// <param name="name">The policy name, compared ordinal (case-sensitive).</param>
    /// <param name="policy">The policy, when one is registered under <paramref name="name"/>; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a policy with the name is registered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public bool TryGetPolicy(string name, [NotNullWhen(true)] out AuthorizationPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _policies.TryGetValue(name, out policy);
    }

    /// <summary>
    /// Gets the effective policy <c>UseAuthorization</c> applies to an endpoint: the endpoint's
    /// <see cref="AuthorizationMetadata"/> items combined against these options, or
    /// <see cref="FallbackPolicy"/> when the endpoint carries none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The items are read outer group first, as routing composes them. The most specific
    /// <see cref="AuthorizationMetadata.AllowAnonymous"/> clears every item declared before it, by the
    /// groups above it or earlier on its own builder, and a requirement declared after it still applies.
    /// Each remaining item contributes its named policy (<see cref="TryGetPolicy"/>), its inline policy
    /// and its roles, or <see cref="DefaultPolicy"/> when it names none of those, plus its authentication
    /// schemes. The result requires all of them, and its <see cref="AuthorizationPolicy.AuthenticationSchemes"/>
    /// are the schemes the middleware authenticates and challenges through: the union, in order, of every
    /// contributing policy's schemes and the items' own.
    /// </para>
    /// <para>
    /// This is the computation the middleware runs, exposed so that a component that describes endpoints
    /// instead of serving them reaches the answer the middleware enforces. When items apply, every call
    /// builds a new policy; the middleware computes it once per endpoint and caches it. A request that
    /// matched no endpoint is not covered: the middleware gives it <see cref="FallbackPolicy"/>.
    /// </para>
    /// </remarks>
    /// <param name="metadata">The endpoint's metadata collection, as routing composed it (outer group first).</param>
    /// <returns>
    /// The effective policy, or <see langword="null"/> when the middleware authorizes the endpoint without
    /// evaluation: its last authorization item is <see cref="AuthorizationMetadata.AllowAnonymous"/>, or it
    /// carries no authorization item and <see cref="FallbackPolicy"/> is <see langword="null"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// An applying item names a policy that is not registered. The middleware fails every request to such
    /// an endpoint with the same exception.
    /// </exception>
    public AuthorizationPolicy? GetEffectivePolicy(IRouterRouteMetadataCollection metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        IReadOnlyList<AuthorizationMetadata> items = metadata.GetOrderedMetadata<AuthorizationMetadata>();

        if (items.Count == 0)
        {
            return _fallbackPolicy;
        }

        int first = 0;

        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].AllowsAnonymous)
            {
                first = i + 1;
                break;
            }
        }

        if (first == items.Count)
        {
            return null;
        }

        AuthorizationPolicyBuilder builder = new();

        for (int i = first; i < items.Count; i++)
        {
            AuthorizationMetadata item = items[i];
            bool requiresDefaultPolicy = true;

            if (item.PolicyName is { } policyName)
            {
                if (!_policies.TryGetValue(policyName, out AuthorizationPolicy? named))
                {
                    throw new InvalidOperationException(
                        $"No authorization policy named '{policyName}' has been registered. " +
                        "Register it with options.AddPolicy(name, policy) in AddAuthorization.");
                }

                builder.Combine(named);
                requiresDefaultPolicy = false;
            }

            if (item.Policy is { } inline)
            {
                builder.Combine(inline);
                requiresDefaultPolicy = false;
            }

            if (item.Roles.Count > 0)
            {
                builder.RequireRole(item.Roles);
                requiresDefaultPolicy = false;
            }

            if (requiresDefaultPolicy)
            {
                builder.Combine(_defaultPolicy);
            }

            if (item.AuthenticationSchemes.Count > 0)
            {
                builder.AddAuthenticationSchemes([.. item.AuthenticationSchemes]);
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Makes the options read-only. <c>AddAuthorization</c> calls this once its callback returns; reads
    /// after that never race a write.
    /// </summary>
    internal void MakeReadOnly() => _isReadOnly = true;

    private void ThrowIfReadOnly()
    {
        if (_isReadOnly)
        {
            throw new InvalidOperationException(
                "Authorization options are read-only once AddAuthorization has captured them. " +
                "Configure every policy inside the AddAuthorization callback.");
        }
    }
}
