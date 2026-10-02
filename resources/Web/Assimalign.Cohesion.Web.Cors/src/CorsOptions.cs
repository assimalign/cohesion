using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Cors;

/// <summary>
/// Builder-time options for the CORS middleware
/// (<see cref="CorsExtensions.UseCors(IWebApplicationPipelineBuilder, Action{CorsOptions})"/>): the default
/// policy, and the named policies endpoints reference through <see cref="CorsMetadata"/>.
/// </summary>
/// <remarks>
/// Composition is dependency-free: the policies are built and validated while <c>UseCors</c> runs, and the
/// middleware keeps its own copy, so changing the options afterwards has no effect. No service container,
/// configuration binding or request-time service location is involved.
/// </remarks>
public sealed class CorsOptions
{
    // Ordinal (case-sensitive) names: a policy name is a developer-chosen key, so exact matching is the
    // least surprising, as it is for rate-limiting and output-cache policy names.
    private readonly Dictionary<string, CorsPolicy> _policies = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the policy applied to requests whose endpoint carries no <see cref="CorsMetadata"/>, including
    /// requests no route matched; <see langword="null"/> when none is registered.
    /// </summary>
    internal CorsPolicy? DefaultPolicy { get; private set; }

    /// <summary>
    /// Gets the named policies.
    /// </summary>
    internal IReadOnlyDictionary<string, CorsPolicy> Policies => _policies;

    /// <summary>
    /// Registers the default policy: the policy for every request whose endpoint carries no
    /// <see cref="CorsMetadata"/>, including requests no route matched and applications without routing.
    /// </summary>
    /// <param name="policy">The policy.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A default policy is already registered.</exception>
    public CorsOptions AddDefaultPolicy(CorsPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (DefaultPolicy is not null)
        {
            throw new InvalidOperationException("A default CORS policy is already registered.");
        }

        DefaultPolicy = policy;
        return this;
    }

    /// <summary>
    /// Builds and registers the default policy: the policy for every request whose endpoint carries no
    /// <see cref="CorsMetadata"/>, including requests no route matched and applications without routing.
    /// </summary>
    /// <param name="configure">Configures the policy.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="configure"/> adds an invalid origin, method or header name.</exception>
    /// <exception cref="InvalidOperationException">
    /// A default policy is already registered, or the configured policy is invalid (see <see cref="CorsPolicyBuilder.Build"/>).
    /// </exception>
    public CorsOptions AddDefaultPolicy(Action<CorsPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        return AddDefaultPolicy(BuildPolicy(configure));
    }

    /// <summary>
    /// Registers a named policy that endpoints reference with <c>RequireCors(name)</c> or
    /// <see cref="CorsMetadata(string)"/>.
    /// </summary>
    /// <param name="name">The policy name, compared with ordinal (case-sensitive) semantics. Must be unique.</param>
    /// <param name="policy">The policy.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A policy with the same name is already registered.</exception>
    public CorsOptions AddPolicy(string name, CorsPolicy policy)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(policy);

        if (!_policies.TryAdd(name, policy))
        {
            throw new InvalidOperationException($"A CORS policy named '{name}' is already registered.");
        }

        return this;
    }

    /// <summary>
    /// Builds and registers a named policy that endpoints reference with <c>RequireCors(name)</c> or
    /// <see cref="CorsMetadata(string)"/>.
    /// </summary>
    /// <param name="name">The policy name, compared with ordinal (case-sensitive) semantics. Must be unique.</param>
    /// <param name="configure">Configures the policy.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is <see langword="null"/> or empty, or <paramref name="configure"/> adds an invalid
    /// origin, method or header name.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A policy with the same name is already registered, or the configured policy is invalid (see
    /// <see cref="CorsPolicyBuilder.Build"/>).
    /// </exception>
    public CorsOptions AddPolicy(string name, Action<CorsPolicyBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(configure);

        return AddPolicy(name, BuildPolicy(configure));
    }

    private static CorsPolicy BuildPolicy(Action<CorsPolicyBuilder> configure)
    {
        CorsPolicyBuilder builder = new();
        configure(builder);
        return builder.Build();
    }
}
