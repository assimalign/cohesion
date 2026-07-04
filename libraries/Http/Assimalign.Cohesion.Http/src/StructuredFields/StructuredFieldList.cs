using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// An RFC 9651 &#167; 3.1 list: an ordered sequence of members, each of which is a single
/// <see cref="StructuredFieldItem"/> or an <see cref="StructuredFieldInnerList"/>
/// (represented uniformly by <see cref="StructuredFieldMember"/>). This is one of the three
/// top-level structured field types.
/// </summary>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct StructuredFieldList
    : IReadOnlyList<StructuredFieldMember>, IEquatable<StructuredFieldList>
{
    private readonly StructuredFieldMember[]? _members;

    /// <summary>An empty list.</summary>
    public static StructuredFieldList Empty => default;

    /// <summary>
    /// Initializes a list from an ordered sequence of members.
    /// </summary>
    /// <param name="members">The members, in order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="members"/> is <see langword="null"/>.</exception>
    public StructuredFieldList(IEnumerable<StructuredFieldMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var buffer = new List<StructuredFieldMember>(members);
        _members = buffer.Count == 0 ? null : buffer.ToArray();
    }

    private StructuredFieldList(StructuredFieldMember[]? members)
    {
        _members = members;
    }

    internal static StructuredFieldList CreateRaw(StructuredFieldMember[] members)
        => new(members.Length == 0 ? null : members);

    /// <summary>Gets the number of members in the list.</summary>
    public int Count => _members?.Length ?? 0;

    /// <summary>Gets the member at the specified position.</summary>
    /// <param name="index">The zero-based position.</param>
    /// <returns>The member at <paramref name="index"/>.</returns>
    public StructuredFieldMember this[int index]
        => (_members ?? throw new ArgumentOutOfRangeException(nameof(index)))[index];

    /// <summary>
    /// Parses <paramref name="input"/> as a top-level RFC 9651 list (&#167; 4.2, field type
    /// <c>list</c>).
    /// </summary>
    /// <param name="input">The field value to parse.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed list.</param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(ReadOnlySpan<char> input, out StructuredFieldList result)
        => TryParse(input, out result, out _);

    /// <summary>
    /// Parses <paramref name="input"/> as a top-level RFC 9651 list (&#167; 4.2, field type
    /// <c>list</c>). On failure, <paramref name="error"/> carries a human-readable explanation.
    /// </summary>
    /// <param name="input">The field value to parse.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed list.</param>
    /// <param name="error">When this method returns <see langword="false"/>, the reason parsing failed.</param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(ReadOnlySpan<char> input, out StructuredFieldList result, out string? error)
        => StructuredFieldParser.TryParseList(input, out result, out error);

    /// <summary>
    /// Parses the combined value of a possibly multi-line header field as a top-level
    /// RFC 9651 list (&#167; 4.2, field type <c>list</c>). Repeated field lines are combined by
    /// comma per RFC 9651 &#167; 4.2.
    /// </summary>
    /// <param name="value">The header field value.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed list.</param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(HttpHeaderValue value, out StructuredFieldList result)
        => TryParse(value.Value.AsSpan(), out result, out _);

    /// <summary>
    /// Parses <paramref name="input"/> as a top-level RFC 9651 list (&#167; 4.2, field type
    /// <c>list</c>).
    /// </summary>
    /// <param name="input">The field value to parse.</param>
    /// <returns>The parsed list.</returns>
    /// <exception cref="HttpException">The input is not a well-formed list.</exception>
    public static StructuredFieldList Parse(ReadOnlySpan<char> input)
    {
        if (!TryParse(input, out StructuredFieldList result, out string? error))
        {
            throw new HttpInvalidStructuredFieldException(error ?? "Malformed structured field list.");
        }
        return result;
    }

    /// <summary>
    /// Parses the combined value of a possibly multi-line header field as a top-level
    /// RFC 9651 list (&#167; 4.2, field type <c>list</c>).
    /// </summary>
    /// <param name="value">The header field value; repeated field lines are combined per RFC 9651 &#167; 4.2.</param>
    /// <returns>The parsed list.</returns>
    /// <exception cref="HttpException">The input is not a well-formed list.</exception>
    public static StructuredFieldList Parse(HttpHeaderValue value) => Parse(value.Value.AsSpan());

    /// <summary>
    /// Serializes this list to its RFC 9651 &#167; 4.1.1 canonical form. Returns the empty
    /// string when the list has no members.
    /// </summary>
    /// <returns>The canonical textual representation.</returns>
    /// <exception cref="HttpException">A member cannot be serialized.</exception>
    public string Serialize()
    {
        if (_members is null)
        {
            return string.Empty;
        }
        var builder = new StringBuilder();
        WriteTo(builder);
        return builder.ToString();
    }

    internal void WriteTo(StringBuilder builder)
    {
        if (_members is null)
        {
            return;
        }
        for (int i = 0; i < _members.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',').Append(' ');
            }
            _members[i].WriteTo(builder);
        }
    }

    /// <inheritdoc />
    public IEnumerator<StructuredFieldMember> GetEnumerator()
    {
        if (_members is null)
        {
            yield break;
        }
        foreach (StructuredFieldMember member in _members)
        {
            yield return member;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public bool Equals(StructuredFieldList other)
    {
        int count = Count;
        if (count != other.Count)
        {
            return false;
        }
        for (int i = 0; i < count; i++)
        {
            if (!_members![i].Equals(other._members![i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StructuredFieldList other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        if (_members is not null)
        {
            foreach (StructuredFieldMember member in _members)
            {
                hash.Add(member);
            }
        }
        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => Serialize();

    /// <summary>Determines whether two lists are equal.</summary>
    /// <param name="left">The first list.</param>
    /// <param name="right">The second list.</param>
    /// <returns><see langword="true"/> if the lists are equal; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(StructuredFieldList left, StructuredFieldList right) => left.Equals(right);

    /// <summary>Determines whether two lists are unequal.</summary>
    /// <param name="left">The first list.</param>
    /// <param name="right">The second list.</param>
    /// <returns><see langword="true"/> if the lists are unequal; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(StructuredFieldList left, StructuredFieldList right) => !left.Equals(right);
}
