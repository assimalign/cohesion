using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A typed bag of feature implementations attached to an HTTP exchange.
/// </summary>
/// <remarks>
/// <para>
/// Higher-layer packages (authentication, sessions, request services,
/// connection lifetime, etc.) define interface contracts and register
/// implementations here; consumers retrieve them by interface type via
/// <see cref="Get{TFeature}"/>.
/// </para>
/// <para>
/// Features sit alongside <see cref="IHttpContext.Items"/> in the
/// extensibility model. Use a feature when the contract is well-defined
/// and the consumer expects a specific shape (auth, session, connection
/// state); use <see cref="IHttpContext.Items"/> for ad-hoc per-request
/// scratch state with no formal contract.
/// </para>
/// </remarks>
public interface IHttpFeatureCollection : IEnumerable<KeyValuePair<Type, object>>
{
    /// <summary>
    /// The current version of the collection. Incremented on every mutation.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Gets or sets a service instance by its type.
    /// </summary>
    /// <param name="key">The service type to retrieve or register.</param>
    /// <returns></returns>
    object? this[Type key] { get; set; }

    /// <summary>
    /// Returns the feature registered under <typeparamref name="TFeature"/>,
    /// or <see langword="null"/> when no implementation has been attached.
    /// </summary>
    /// <typeparam name="TFeature">The feature contract interface.</typeparam>
    TFeature? Get<TFeature>();

    /// <summary>
    /// Attaches <paramref name="feature"/> as the implementation for
    /// <typeparamref name="TFeature"/>. Replaces any previously registered
    /// implementation of the same type. Pass <see langword="null"/> to
    /// remove an existing registration.
    /// </summary>
    /// <typeparam name="TFeature">The feature contract interface.</typeparam>
    /// <param name="feature">The implementation to register, or <see langword="null"/> to clear.</param>
    void Set<TFeature>(TFeature? feature);
}
