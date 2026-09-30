using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

using Assimalign.Cohesion.Web.Routing.Metadata;

namespace Assimalign.Cohesion.Web.Routing.Internal;

/// <summary>
/// The metadata of a route mapped through a convention builder: route-level items accumulate until
/// the route table is built, and the collection then composes, once, the metadata of every group the
/// route belongs to followed by its own items.
/// </summary>
/// <remarks>
/// Composition is deferred so metadata conventions do not depend on call order (a group's metadata
/// reaches children mapped before it; a route's metadata can be attached after it is mapped).
/// <see cref="RouterBuilder.Build"/> resolves every deferred collection before it constructs the
/// router, and a read before that resolves it too, so a route never observes two different
/// metadata sets. Adding metadata after resolution throws, because it would silently not apply.
/// </remarks>
internal sealed class DeferredRouteMetadata : IRouterRouteMetadataCollection
{
    private readonly RouterGroupBuilder? _group;
    private readonly Lock _lock = new();

    // The route's own items until resolution; null afterwards.
    private List<object>? _items;
    private RouterRouteMetadataCollection? _resolved;

    /// <summary>
    /// Initializes deferred metadata for a route mapped directly (no group) or through
    /// <paramref name="group"/>.
    /// </summary>
    /// <param name="group">The innermost group the route belongs to, or <see langword="null"/>.</param>
    /// <param name="initial">Route-level items supplied when the route was mapped, or <see langword="null"/>.</param>
    public DeferredRouteMetadata(RouterGroupBuilder? group, IRouterRouteMetadataCollection? initial)
    {
        _group = group;
        _items = new List<object>(initial?.Count ?? 0);

        if (initial is not null)
        {
            foreach (object item in initial)
            {
                _items.Add(item);
            }
        }
    }

    /// <inheritdoc />
    public int Count => Resolve().Count;

    /// <inheritdoc />
    public object this[int index] => Resolve()[index];

    /// <summary>
    /// Appends route-level metadata items.
    /// </summary>
    /// <param name="items">The validated items to append.</param>
    /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
    public void Add(object[] items)
    {
        lock (_lock)
        {
            if (_items is null)
            {
                throw RouteTableBuilt();
            }

            _items.AddRange(items);
        }
    }

    /// <summary>
    /// Composes the group chain's metadata (outermost group first) followed by the route's own items,
    /// once. Later calls return the same collection.
    /// </summary>
    /// <returns>The resolved, immutable metadata collection.</returns>
    public RouterRouteMetadataCollection Resolve()
    {
        RouterRouteMetadataCollection? resolved = Volatile.Read(ref _resolved);
        if (resolved is not null)
        {
            return resolved;
        }

        lock (_lock)
        {
            if (_resolved is null)
            {
                List<object> items = new();
                _group?.CollectMetadata(items);
                items.AddRange(_items!);

                _items = null;
                Volatile.Write(ref _resolved, items.Count == 0 ? RouterRouteMetadataCollection.Empty : new RouterRouteMetadataCollection(items));
            }

            return _resolved!;
        }
    }

    /// <inheritdoc />
    public TMetadata? GetMetadata<TMetadata>() where TMetadata : class => Resolve().GetMetadata<TMetadata>();

    /// <inheritdoc />
    public IReadOnlyList<TMetadata> GetOrderedMetadata<TMetadata>() where TMetadata : class => Resolve().GetOrderedMetadata<TMetadata>();

    /// <inheritdoc />
    public IEnumerator<object> GetEnumerator() => ((IEnumerable<object>)Resolve()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Validates items passed to a <c>WithMetadata</c> call.
    /// </summary>
    /// <param name="items">The items to validate.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="items"/> contains a <see langword="null"/> entry.</exception>
    public static void Validate(object[] items)
    {
        ArgumentNullException.ThrowIfNull(items);

        foreach (object item in items)
        {
            if (item is null)
            {
                throw new ArgumentException("Endpoint metadata items must not be null.", nameof(items));
            }
        }
    }

    /// <summary>
    /// Creates the exception thrown when metadata is attached after the route table was built.
    /// </summary>
    /// <returns>The exception to throw.</returns>
    public static InvalidOperationException RouteTableBuilt() => new(
        "Endpoint metadata cannot be added: the route table has already been built, so it would not apply. " +
        "A web application builds its router when its request pipeline is built at startup, so attach metadata " +
        "to routes and groups before the application starts.");
}
