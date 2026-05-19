using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A name-keyed collection of <see cref="IHttpFeature"/> implementations
/// attached to an HTTP exchange.
/// </summary>
/// <remarks>
/// <para>
/// Higher-layer packages (authentication, sessions, cookies, request
/// services, connection lifetime, etc.) define <see cref="IHttpFeature"/>
/// contracts and register implementations here. The slot key is
/// <see cref="IHttpFeature.Name"/>; registering a feature with the same
/// name as an existing one replaces it.
/// </para>
/// <para>
/// Features sit alongside <see cref="IHttpContext.Items"/> in the
/// extensibility model. Use a feature when the contract is well-defined
/// and the consumer expects a specific shape (auth, session, cookies,
/// connection state); use <see cref="IHttpContext.Items"/> for ad-hoc
/// per-request scratch state with no formal contract.
/// </para>
/// <para>
/// Most call sites prefer type-based access via the
/// <see cref="HttpFeatureCollectionExtensions.Get{TFeature}"/> /
/// <see cref="HttpFeatureCollectionExtensions.Set{TFeature}(IHttpFeatureCollection, TFeature?)"/>
/// extension helpers, which resolve the contract interface to a
/// registered implementation by enumerating the collection. The
/// name-keyed surface here is the storage primitive; the type-based
/// helpers are the ergonomic skin on top.
/// </para>
/// </remarks>
public interface IHttpFeatureCollection : IEnumerable<IHttpFeature>
{
    /// <summary>
    /// The current version of the collection. Incremented on every
    /// mutation. Consumers can cache this and compare on revisit to detect
    /// changes without re-enumerating.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Returns the feature registered under <paramref name="name"/>, or
    /// <see langword="null"/> when no such feature is registered.
    /// </summary>
    /// <param name="name">The feature name. Case-sensitive.</param>
    /// <returns>The registered feature, or <see langword="null"/>.</returns>
    IHttpFeature? Get(string name);

    /// <summary>
    /// Registers <paramref name="feature"/> using its
    /// <see cref="IHttpFeature.Name"/> as the slot. Replaces any
    /// previously-registered feature with the same name. Passing
    /// <see langword="null"/> is a no-op &#8211; removal is name-keyed
    /// and goes through <see cref="Remove"/>.
    /// </summary>
    /// <param name="feature">The implementation to register, or
    /// <see langword="null"/> for a no-op.</param>
    void Set(IHttpFeature? feature);

    /// <summary>
    /// Removes the feature registered under <paramref name="name"/>.
    /// </summary>
    /// <param name="name">The feature name to remove.</param>
    /// <returns><see langword="true"/> if a feature was removed;
    /// <see langword="false"/> if no feature was registered under that
    /// name.</returns>
    bool Remove(string name);
}
