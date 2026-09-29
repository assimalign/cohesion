using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// An RFC 9651 &#167; 3.1.1 inner list: a parenthesized, whitespace-separated sequence of
/// <see cref="StructuredFieldItem"/> values with its own
/// <see cref="StructuredFieldParameters"/>. An inner list can appear as a member of a
/// <see cref="StructuredFieldList"/> or as a value in a <see cref="StructuredFieldDictionary"/>.
/// </summary>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct StructuredFieldInnerList
    : IReadOnlyList<StructuredFieldItem>, IEquatable<StructuredFieldInnerList>
{
    private readonly StructuredFieldItem[]? _items;

    /// <summary>
    /// Initializes an inner list.
    /// </summary>
    /// <param name="items">The items, in order.</param>
    /// <param name="parameters">The parameters attached to the inner list.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    public StructuredFieldInnerList(IEnumerable<StructuredFieldItem> items, StructuredFieldParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(items);
        var buffer = new List<StructuredFieldItem>(items);
        _items = buffer.Count == 0 ? null : buffer.ToArray();
        Parameters = parameters;
    }

    private StructuredFieldInnerList(StructuredFieldItem[]? items, StructuredFieldParameters parameters)
    {
        _items = items;
        Parameters = parameters;
    }

    internal static StructuredFieldInnerList CreateRaw(StructuredFieldItem[] items, StructuredFieldParameters parameters)
        => new(items.Length == 0 ? null : items, parameters);

    /// <summary>Gets the parameters attached to this inner list.</summary>
    public StructuredFieldParameters Parameters { get; }

    /// <summary>Gets the number of items in the inner list.</summary>
    public int Count => _items?.Length ?? 0;

    /// <summary>Gets the item at the specified position.</summary>
    /// <param name="index">The zero-based position.</param>
    /// <returns>The item at <paramref name="index"/>.</returns>
    public StructuredFieldItem this[int index]
        => (_items ?? throw new ArgumentOutOfRangeException(nameof(index)))[index];

    /// <summary>
    /// Serializes this inner list to its RFC 9651 &#167; 4.1.1.1 canonical form.
    /// </summary>
    /// <returns>The canonical textual representation.</returns>
    /// <exception cref="HttpException">An element cannot be serialized.</exception>
    public string Serialize()
    {
        var builder = new StringBuilder();
        WriteTo(builder);
        return builder.ToString();
    }

    internal void WriteTo(StringBuilder builder)
    {
        builder.Append('(');
        if (_items is not null)
        {
            for (int i = 0; i < _items.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(' ');
                }
                _items[i].WriteTo(builder);
            }
        }
        builder.Append(')');
        Parameters.WriteTo(builder);
    }

    /// <inheritdoc />
    public IEnumerator<StructuredFieldItem> GetEnumerator()
    {
        if (_items is null)
        {
            yield break;
        }
        foreach (StructuredFieldItem item in _items)
        {
            yield return item;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public bool Equals(StructuredFieldInnerList other)
    {
        int count = Count;
        if (count != other.Count || !Parameters.Equals(other.Parameters))
        {
            return false;
        }
        for (int i = 0; i < count; i++)
        {
            if (!_items![i].Equals(other._items![i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StructuredFieldInnerList other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        if (_items is not null)
        {
            foreach (StructuredFieldItem item in _items)
            {
                hash.Add(item);
            }
        }
        hash.Add(Parameters);
        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => Serialize();

    /// <summary>Determines whether two inner lists are equal.</summary>
    /// <param name="left">The first inner list.</param>
    /// <param name="right">The second inner list.</param>
    /// <returns><see langword="true"/> if the inner lists are equal; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(StructuredFieldInnerList left, StructuredFieldInnerList right) => left.Equals(right);

    /// <summary>Determines whether two inner lists are unequal.</summary>
    /// <param name="left">The first inner list.</param>
    /// <param name="right">The second inner list.</param>
    /// <returns><see langword="true"/> if the inner lists are unequal; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(StructuredFieldInnerList left, StructuredFieldInnerList right) => !left.Equals(right);
}
