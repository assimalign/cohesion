using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Default immutable <see cref="IRouterRouteMetadataCollection"/> implementation
/// backed by a copied <see cref="object"/> array.
/// </summary>
/// <remarks>
/// <para>
/// The supplied items are copied into an internal array at construction, so the
/// collection is not affected by later mutation of the source sequence and is
/// safe to share across threads for the lifetime of the route it belongs to.
/// </para>
/// <para>
/// Lookups (<see cref="GetMetadata{TMetadata}"/>,
/// <see cref="GetOrderedMetadata{TMetadata}"/>) are linear <c>is</c>-test scans
/// with no reflection, keeping the type AOT- and trimming-safe. Enumeration is
/// allocation-free through the value-type <see cref="Enumerator"/>.
/// </para>
/// </remarks>
public sealed class RouterRouteMetadataCollection : IRouterRouteMetadataCollection
{
    /// <summary>
    /// A shared empty metadata collection. Used as the default for routes that
    /// declare no metadata.
    /// </summary>
    public static readonly RouterRouteMetadataCollection Empty = new(Array.Empty<object>());

    private readonly object[] _items;

    /// <summary>
    /// Initializes a new collection from the supplied metadata items.
    /// </summary>
    /// <param name="items">The metadata items to store. Must not contain <see langword="null"/> entries.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="items"/> contains a <see langword="null"/> entry.</exception>
    public RouterRouteMetadataCollection(params object[] items)
        : this((IEnumerable<object>)items)
    {
    }

    /// <summary>
    /// Initializes a new collection from the supplied metadata items.
    /// </summary>
    /// <param name="items">The metadata items to store. Must not contain <see langword="null"/> entries.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="items"/> contains a <see langword="null"/> entry.</exception>
    public RouterRouteMetadataCollection(IEnumerable<object> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        _items = items is object[] array
            ? (object[])array.Clone()
            : new List<object>(items).ToArray();

        for (int index = 0; index < _items.Length; index++)
        {
            if (_items[index] is null)
            {
                throw new ArgumentException("Endpoint metadata items must not be null.", nameof(items));
            }
        }
    }

    /// <inheritdoc />
    public object this[int index] => _items[index];

    /// <inheritdoc />
    public int Count => _items.Length;

    /// <inheritdoc />
    public TMetadata? GetMetadata<TMetadata>() where TMetadata : class
    {
        // Last-wins: scan from the end so the most-recently-registered item
        // (typically the most specific scope) is returned first.
        for (int index = _items.Length - 1; index >= 0; index--)
        {
            if (_items[index] is TMetadata metadata)
            {
                return metadata;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<TMetadata> GetOrderedMetadata<TMetadata>() where TMetadata : class
    {
        List<TMetadata>? matches = null;

        for (int index = 0; index < _items.Length; index++)
        {
            if (_items[index] is TMetadata metadata)
            {
                (matches ??= new List<TMetadata>()).Add(metadata);
            }
        }

        return matches ?? (IReadOnlyList<TMetadata>)Array.Empty<TMetadata>();
    }

    /// <summary>
    /// Returns an allocation-free enumerator over the metadata items in
    /// registration order.
    /// </summary>
    /// <returns>A value-type enumerator.</returns>
    public Enumerator GetEnumerator() => new Enumerator(_items);

    IEnumerator<object> IEnumerable<object>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Enumerates the metadata items of an <see cref="RouterRouteMetadataCollection"/>
    /// without allocating.
    /// </summary>
    public struct Enumerator : IEnumerator<object>
    {
        private readonly object[] _items;
        private int _index;

        internal Enumerator(object[] items)
        {
            _items = items;
            _index = -1;
            Current = default!;
        }

        /// <inheritdoc />
        public object Current { get; private set; }

        object IEnumerator.Current => Current;

        /// <inheritdoc />
        public bool MoveNext()
        {
            int next = _index + 1;
            if ((uint)next < (uint)_items.Length)
            {
                _index = next;
                Current = _items[next];
                return true;
            }

            _index = _items.Length;
            Current = default!;
            return false;
        }

        /// <inheritdoc />
        public void Reset()
        {
            _index = -1;
            Current = default!;
        }

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }
}
