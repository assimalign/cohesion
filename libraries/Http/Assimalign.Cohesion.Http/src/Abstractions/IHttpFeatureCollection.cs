using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

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
public interface IHttpFeatureCollection : IEnumerable<IHttpFeature>
{
    /// <summary>
    /// The current version of the collection. Incremented on every mutation.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    IHttpFeature? Get(string name);

    /// <summary>
    /// Attaches <paramref name="feature"/> as the implementation for
    /// <see cref="IHttpFeature"/>. Replaces any previously registered
    /// implementation of the same type. Pass <see langword="null"/> to
    /// remove an existing registration.
    /// </summary>
    /// <typeparam name="TFeature">The feature contract interface.</typeparam>
    /// <param name="feature">The implementation to register, or <see langword="null"/> to clear.</param>
    void Set(IHttpFeature? feature);
}
