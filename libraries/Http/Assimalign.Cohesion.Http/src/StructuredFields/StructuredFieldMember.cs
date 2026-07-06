using System;
using System.Diagnostics;
using System.Text;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A member of an RFC 9651 <see cref="StructuredFieldList"/> or the value of a member of a
/// <see cref="StructuredFieldDictionary"/>: either a single <see cref="StructuredFieldItem"/>
/// or an <see cref="StructuredFieldInnerList"/> (RFC 9651 &#167; 3.1, &#167; 3.2). Inspect
/// <see cref="IsInnerList"/> before reading <see cref="Item"/> or <see cref="InnerList"/>.
/// </summary>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct StructuredFieldMember : IEquatable<StructuredFieldMember>
{
    private readonly StructuredFieldItem _item;
    private readonly StructuredFieldInnerList _innerList;

    private StructuredFieldMember(bool isInnerList, StructuredFieldItem item, StructuredFieldInnerList innerList)
    {
        IsInnerList = isInnerList;
        _item = item;
        _innerList = innerList;
    }

    /// <summary>
    /// Creates a member that wraps a single item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The member.</returns>
    public static StructuredFieldMember FromItem(StructuredFieldItem item)
        => new(false, item, default);

    /// <summary>
    /// Creates a member that wraps an inner list.
    /// </summary>
    /// <param name="innerList">The inner list.</param>
    /// <returns>The member.</returns>
    public static StructuredFieldMember FromInnerList(StructuredFieldInnerList innerList)
        => new(true, default, innerList);

    /// <summary>Gets a value indicating whether this member is an inner list (rather than a single item).</summary>
    public bool IsInnerList { get; }

    /// <summary>Gets the wrapped item.</summary>
    /// <returns>The item.</returns>
    /// <exception cref="InvalidOperationException">This member is an inner list.</exception>
    public StructuredFieldItem Item
        => !IsInnerList ? _item : throw new InvalidOperationException("Structured field member is an inner list, not an item.");

    /// <summary>Gets the wrapped inner list.</summary>
    /// <returns>The inner list.</returns>
    /// <exception cref="InvalidOperationException">This member is a single item.</exception>
    public StructuredFieldInnerList InnerList
        => IsInnerList ? _innerList : throw new InvalidOperationException("Structured field member is an item, not an inner list.");

    /// <summary>
    /// Gets the parameters attached to this member — the item's parameters or the inner
    /// list's parameters, depending on <see cref="IsInnerList"/>.
    /// </summary>
    public StructuredFieldParameters Parameters => IsInnerList ? _innerList.Parameters : _item.Parameters;

    /// <summary>
    /// Serializes this member to its RFC 9651 &#167; 4.1 canonical form.
    /// </summary>
    /// <returns>The canonical textual representation.</returns>
    /// <exception cref="HttpException">The member cannot be serialized.</exception>
    public string Serialize()
    {
        var builder = new StringBuilder();
        WriteTo(builder);
        return builder.ToString();
    }

    internal void WriteTo(StringBuilder builder)
    {
        if (IsInnerList)
        {
            _innerList.WriteTo(builder);
        }
        else
        {
            _item.WriteTo(builder);
        }
    }

    /// <inheritdoc />
    public bool Equals(StructuredFieldMember other)
    {
        if (IsInnerList != other.IsInnerList)
        {
            return false;
        }
        return IsInnerList ? _innerList.Equals(other._innerList) : _item.Equals(other._item);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StructuredFieldMember other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => IsInnerList ? HashCode.Combine(true, _innerList) : HashCode.Combine(false, _item);

    /// <inheritdoc />
    public override string ToString() => Serialize();

    /// <summary>Determines whether two members are equal.</summary>
    /// <param name="left">The first member.</param>
    /// <param name="right">The second member.</param>
    /// <returns><see langword="true"/> if the members are equal; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(StructuredFieldMember left, StructuredFieldMember right) => left.Equals(right);

    /// <summary>Determines whether two members are unequal.</summary>
    /// <param name="left">The first member.</param>
    /// <param name="right">The second member.</param>
    /// <returns><see langword="true"/> if the members are unequal; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(StructuredFieldMember left, StructuredFieldMember right) => !left.Equals(right);
}
