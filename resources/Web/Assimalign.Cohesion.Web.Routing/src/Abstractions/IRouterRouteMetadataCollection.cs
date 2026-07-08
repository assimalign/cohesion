using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// An immutable, ordered, strongly-typed collection of arbitrary metadata
/// objects attached to a route (endpoint).
/// </summary>
/// <remarks>
/// <para>
/// Endpoint metadata is the seam through which cross-cutting concerns &#8211;
/// authorization, content negotiation, API documentation (OpenAPI), diagnostics
/// and observability &#8211; discover per-endpoint policy <em>without runtime
/// reflection</em>. Producers (route mapping, route groups, host-matching,
/// source generators) attach plain metadata objects to a route at build time;
/// consumers read them back with <see cref="GetMetadata{TMetadata}"/> and
/// <see cref="GetOrderedMetadata{TMetadata}"/>, both of which resolve by
/// assignability using <c>is</c>-tests only. There is no reflection, no dynamic
/// activation, and no linker-unfriendly type discovery, so the collection is
/// safe under NativeAOT and trimming.
/// </para>
/// <para>
/// The collection is immutable: once constructed its contents never change.
/// Composition (for example merging route-group metadata with endpoint
/// metadata) is performed by building a new collection from the concatenated
/// items rather than by mutating an existing one.
/// </para>
/// <para>
/// Ordering is significant. Items are stored in the order they were supplied,
/// which is the order in which enumeration and <see cref="GetOrderedMetadata{TMetadata}"/>
/// yield them. <see cref="GetMetadata{TMetadata}"/> applies a <em>last-wins</em>
/// rule: it returns the last item in registration order that is assignable to
/// the requested type. Producers that layer broader-scope metadata first
/// (for example group-level) and narrower-scope metadata last (for example
/// endpoint-level) therefore get the intuitive "the most specific declaration
/// wins" behavior.
/// </para>
/// </remarks>
public interface IRouterRouteMetadataCollection : IReadOnlyList<object>
{
    /// <summary>
    /// Gets the most recently registered metadata item assignable to
    /// <typeparamref name="TMetadata"/>, or <see langword="null"/> when the
    /// collection contains no such item.
    /// </summary>
    /// <typeparam name="TMetadata">The metadata contract or concrete type to resolve.</typeparam>
    /// <returns>
    /// The last item (in registration order) assignable to
    /// <typeparamref name="TMetadata"/>, or <see langword="null"/> when none is present.
    /// </returns>
    /// <remarks>
    /// Resolution is a <em>last-wins</em> scan performed with <c>is</c>-tests
    /// only, so it remains reflection-free and AOT-safe.
    /// </remarks>
    TMetadata? GetMetadata<TMetadata>() where TMetadata : class;

    /// <summary>
    /// Gets every metadata item assignable to <typeparamref name="TMetadata"/>,
    /// in registration order.
    /// </summary>
    /// <typeparam name="TMetadata">The metadata contract or concrete type to resolve.</typeparam>
    /// <returns>
    /// A read-only list of the matching items in registration order; an empty
    /// list when none is present. Never <see langword="null"/>.
    /// </returns>
    IReadOnlyList<TMetadata> GetOrderedMetadata<TMetadata>() where TMetadata : class;
}
