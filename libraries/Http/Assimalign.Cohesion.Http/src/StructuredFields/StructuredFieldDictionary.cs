using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// An RFC 9651 &#167; 3.2 dictionary: an ordered map from keys to members, each member being
/// a single <see cref="StructuredFieldItem"/> or an <see cref="StructuredFieldInnerList"/>
/// (represented uniformly by <see cref="StructuredFieldMember"/>). Keys are unique and their
/// first-seen order is preserved; a repeated key keeps its position and takes the last value.
/// This is one of the three top-level structured field types.
/// </summary>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct StructuredFieldDictionary
    : IReadOnlyList<KeyValuePair<string, StructuredFieldMember>>, IEquatable<StructuredFieldDictionary>
{
    private readonly KeyValuePair<string, StructuredFieldMember>[]? _members;

    /// <summary>An empty dictionary.</summary>
    public static StructuredFieldDictionary Empty => default;

    /// <summary>
    /// Initializes a dictionary from an ordered sequence of key/member pairs.
    /// </summary>
    /// <param name="members">The members, in order. Keys must match the RFC 9651 <c>key</c>
    /// grammar; a repeated key keeps its first position and takes the last value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="members"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A key does not match the RFC 9651 <c>key</c> grammar.</exception>
    public StructuredFieldDictionary(IEnumerable<KeyValuePair<string, StructuredFieldMember>> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var list = new List<KeyValuePair<string, StructuredFieldMember>>();
        foreach (KeyValuePair<string, StructuredFieldMember> pair in members)
        {
            if (pair.Key is null || !StructuredFieldGrammar.IsValidKey(pair.Key))
            {
                throw new ArgumentException($"'{pair.Key}' is not a valid RFC 9651 key.", nameof(members));
            }
            Upsert(list, pair.Key, pair.Value);
        }
        _members = list.Count == 0 ? null : list.ToArray();
    }

    private StructuredFieldDictionary(KeyValuePair<string, StructuredFieldMember>[]? members)
    {
        _members = members;
    }

    internal static StructuredFieldDictionary CreateRaw(KeyValuePair<string, StructuredFieldMember>[] members)
        => new(members.Length == 0 ? null : members);

    internal static void Upsert(List<KeyValuePair<string, StructuredFieldMember>> list, string key, StructuredFieldMember value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Key, key, StringComparison.Ordinal))
            {
                list[i] = new KeyValuePair<string, StructuredFieldMember>(key, value);
                return;
            }
        }
        list.Add(new KeyValuePair<string, StructuredFieldMember>(key, value));
    }

    /// <summary>Gets the number of members in the dictionary.</summary>
    public int Count => _members?.Length ?? 0;

    /// <summary>Gets the member at the specified position.</summary>
    /// <param name="index">The zero-based position.</param>
    /// <returns>The key/member pair at <paramref name="index"/>.</returns>
    public KeyValuePair<string, StructuredFieldMember> this[int index]
        => (_members ?? throw new ArgumentOutOfRangeException(nameof(index)))[index];

    /// <summary>Attempts to get the member associated with <paramref name="key"/>.</summary>
    /// <param name="key">The member key.</param>
    /// <param name="value">When this method returns <see langword="true"/>, the associated member.</param>
    /// <returns><see langword="true"/> if the key is present; otherwise <see langword="false"/>.</returns>
    public bool TryGetValue(string key, out StructuredFieldMember value)
    {
        if (_members is not null && key is not null)
        {
            foreach (KeyValuePair<string, StructuredFieldMember> pair in _members)
            {
                if (string.Equals(pair.Key, key, StringComparison.Ordinal))
                {
                    value = pair.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    /// <summary>Determines whether <paramref name="key"/> is present.</summary>
    /// <param name="key">The member key.</param>
    /// <returns><see langword="true"/> if the key is present; otherwise <see langword="false"/>.</returns>
    public bool ContainsKey(string key) => TryGetValue(key, out _);

    /// <summary>
    /// Parses <paramref name="input"/> as a top-level RFC 9651 dictionary (&#167; 4.2, field type
    /// <c>dictionary</c>).
    /// </summary>
    /// <param name="input">The field value to parse.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed dictionary.</param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(ReadOnlySpan<char> input, out StructuredFieldDictionary result)
        => TryParse(input, out result, out _);

    /// <summary>
    /// Parses <paramref name="input"/> as a top-level RFC 9651 dictionary (&#167; 4.2, field type
    /// <c>dictionary</c>). On failure, <paramref name="error"/> carries a human-readable explanation.
    /// </summary>
    /// <param name="input">The field value to parse.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed dictionary.</param>
    /// <param name="error">When this method returns <see langword="false"/>, the reason parsing failed.</param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(ReadOnlySpan<char> input, out StructuredFieldDictionary result, out string? error)
        => StructuredFieldParser.TryParseDictionary(input, out result, out error);

    /// <summary>
    /// Parses the combined value of a possibly multi-line header field as a top-level
    /// RFC 9651 dictionary (&#167; 4.2, field type <c>dictionary</c>). Repeated field lines are
    /// combined by comma per RFC 9651 &#167; 4.2.
    /// </summary>
    /// <param name="value">The header field value.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed dictionary.</param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(HttpHeaderValue value, out StructuredFieldDictionary result)
        => TryParse(value.Value.AsSpan(), out result, out _);

    /// <summary>
    /// Parses <paramref name="input"/> as a top-level RFC 9651 dictionary (&#167; 4.2, field type
    /// <c>dictionary</c>).
    /// </summary>
    /// <param name="input">The field value to parse.</param>
    /// <returns>The parsed dictionary.</returns>
    /// <exception cref="HttpException">The input is not a well-formed dictionary.</exception>
    public static StructuredFieldDictionary Parse(ReadOnlySpan<char> input)
    {
        if (!TryParse(input, out StructuredFieldDictionary result, out string? error))
        {
            throw new HttpInvalidStructuredFieldException(error ?? "Malformed structured field dictionary.");
        }
        return result;
    }

    /// <summary>
    /// Parses the combined value of a possibly multi-line header field as a top-level
    /// RFC 9651 dictionary (&#167; 4.2, field type <c>dictionary</c>).
    /// </summary>
    /// <param name="value">The header field value; repeated field lines are combined per RFC 9651 &#167; 4.2.</param>
    /// <returns>The parsed dictionary.</returns>
    /// <exception cref="HttpException">The input is not a well-formed dictionary.</exception>
    public static StructuredFieldDictionary Parse(HttpHeaderValue value) => Parse(value.Value.AsSpan());

    /// <summary>
    /// Serializes this dictionary to its RFC 9651 &#167; 4.1.2 canonical form. Returns the empty
    /// string when the dictionary has no members.
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
            KeyValuePair<string, StructuredFieldMember> pair = _members[i];
            StructuredFieldGrammar.WriteKey(builder, pair.Key);

            StructuredFieldMember member = pair.Value;
            // A member whose value is Boolean true is serialized as the bare key plus its
            // parameters, omitting the "=?1" (RFC 9651 §4.1.2).
            if (!member.IsInnerList
                && member.Item.Value.Type == StructuredFieldType.Boolean
                && member.Item.Value.AsBoolean())
            {
                member.Item.Parameters.WriteTo(builder);
                continue;
            }

            builder.Append('=');
            member.WriteTo(builder);
        }
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, StructuredFieldMember>> GetEnumerator()
    {
        if (_members is null)
        {
            yield break;
        }
        foreach (KeyValuePair<string, StructuredFieldMember> pair in _members)
        {
            yield return pair;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public bool Equals(StructuredFieldDictionary other)
    {
        int count = Count;
        if (count != other.Count)
        {
            return false;
        }
        for (int i = 0; i < count; i++)
        {
            if (!string.Equals(_members![i].Key, other._members![i].Key, StringComparison.Ordinal)
                || !_members[i].Value.Equals(other._members[i].Value))
            {
                return false;
            }
        }
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StructuredFieldDictionary other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        if (_members is not null)
        {
            foreach (KeyValuePair<string, StructuredFieldMember> pair in _members)
            {
                hash.Add(pair.Key, StringComparer.Ordinal);
                hash.Add(pair.Value);
            }
        }
        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => Serialize();

    /// <summary>Determines whether two dictionaries are equal.</summary>
    /// <param name="left">The first dictionary.</param>
    /// <param name="right">The second dictionary.</param>
    /// <returns><see langword="true"/> if the dictionaries are equal; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(StructuredFieldDictionary left, StructuredFieldDictionary right) => left.Equals(right);

    /// <summary>Determines whether two dictionaries are unequal.</summary>
    /// <param name="left">The first dictionary.</param>
    /// <param name="right">The second dictionary.</param>
    /// <returns><see langword="true"/> if the dictionaries are unequal; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(StructuredFieldDictionary left, StructuredFieldDictionary right) => !left.Equals(right);
}
