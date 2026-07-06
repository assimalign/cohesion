using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The ordered map of parameters attached to an <see cref="StructuredFieldItem"/>, an
/// <see cref="StructuredFieldInnerList"/>, or a member of a list or dictionary
/// (RFC 9651 &#167; 3.1.2). Keys are unique and their first-seen order is preserved; a
/// repeated key keeps its position and takes the last value.
/// </summary>
public readonly struct StructuredFieldParameters
    : IReadOnlyList<KeyValuePair<string, StructuredFieldBareItem>>, IEquatable<StructuredFieldParameters>
{
    private readonly KeyValuePair<string, StructuredFieldBareItem>[]? _pairs;

    /// <summary>An empty parameter map.</summary>
    public static StructuredFieldParameters Empty => default;

    /// <summary>
    /// Initializes a parameter map from an ordered sequence of key/value pairs.
    /// </summary>
    /// <param name="pairs">The parameters, in order. Keys must match the RFC 9651 <c>key</c>
    /// grammar; a repeated key keeps its first position and takes the last value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pairs"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A key does not match the RFC 9651 <c>key</c> grammar.</exception>
    public StructuredFieldParameters(IEnumerable<KeyValuePair<string, StructuredFieldBareItem>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var list = new List<KeyValuePair<string, StructuredFieldBareItem>>();
        foreach (KeyValuePair<string, StructuredFieldBareItem> pair in pairs)
        {
            if (pair.Key is null || !StructuredFieldGrammar.IsValidKey(pair.Key))
            {
                throw new ArgumentException($"'{pair.Key}' is not a valid RFC 9651 key.", nameof(pairs));
            }
            Upsert(list, pair.Key, pair.Value);
        }
        _pairs = list.Count == 0 ? null : list.ToArray();
    }

    private StructuredFieldParameters(KeyValuePair<string, StructuredFieldBareItem>[]? pairs)
    {
        _pairs = pairs;
    }

    internal static StructuredFieldParameters CreateRaw(KeyValuePair<string, StructuredFieldBareItem>[]? pairs)
        => new(pairs is { Length: 0 } ? null : pairs);

    internal static void Upsert(List<KeyValuePair<string, StructuredFieldBareItem>> list, string key, StructuredFieldBareItem value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Key, key, StringComparison.Ordinal))
            {
                list[i] = new KeyValuePair<string, StructuredFieldBareItem>(key, value);
                return;
            }
        }
        list.Add(new KeyValuePair<string, StructuredFieldBareItem>(key, value));
    }

    /// <summary>Gets the number of parameters.</summary>
    public int Count => _pairs?.Length ?? 0;

    /// <summary>Gets the parameter at the specified position.</summary>
    /// <param name="index">The zero-based position.</param>
    /// <returns>The key/value pair at <paramref name="index"/>.</returns>
    public KeyValuePair<string, StructuredFieldBareItem> this[int index]
        => (_pairs ?? throw new ArgumentOutOfRangeException(nameof(index)))[index];

    /// <summary>Attempts to get the value associated with <paramref name="key"/>.</summary>
    /// <param name="key">The parameter key.</param>
    /// <param name="value">When this method returns <see langword="true"/>, the associated value.</param>
    /// <returns><see langword="true"/> if the key is present; otherwise <see langword="false"/>.</returns>
    public bool TryGetValue(string key, out StructuredFieldBareItem value)
    {
        if (_pairs is not null && key is not null)
        {
            foreach (KeyValuePair<string, StructuredFieldBareItem> pair in _pairs)
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
    /// <param name="key">The parameter key.</param>
    /// <returns><see langword="true"/> if the key is present; otherwise <see langword="false"/>.</returns>
    public bool ContainsKey(string key) => TryGetValue(key, out _);

    /// <summary>
    /// Serializes these parameters to their RFC 9651 &#167; 4.1.1.2 canonical form, including the
    /// leading <c>;</c> before each parameter. Returns the empty string when there are no
    /// parameters.
    /// </summary>
    /// <returns>The canonical textual representation.</returns>
    /// <exception cref="HttpException">A parameter cannot be serialized.</exception>
    public string Serialize()
    {
        if (_pairs is null)
        {
            return string.Empty;
        }
        var builder = new StringBuilder();
        WriteTo(builder);
        return builder.ToString();
    }

    internal void WriteTo(StringBuilder builder)
    {
        if (_pairs is null)
        {
            return;
        }
        foreach (KeyValuePair<string, StructuredFieldBareItem> pair in _pairs)
        {
            builder.Append(';');
            StructuredFieldGrammar.WriteKey(builder, pair.Key);
            // A Boolean true parameter is serialized as the bare key (RFC 9651 §4.1.1.2).
            if (pair.Value.Type == StructuredFieldType.Boolean && pair.Value.AsBoolean())
            {
                continue;
            }
            builder.Append('=');
            pair.Value.WriteTo(builder);
        }
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, StructuredFieldBareItem>> GetEnumerator()
    {
        if (_pairs is null)
        {
            yield break;
        }
        foreach (KeyValuePair<string, StructuredFieldBareItem> pair in _pairs)
        {
            yield return pair;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public bool Equals(StructuredFieldParameters other)
    {
        int count = Count;
        if (count != other.Count)
        {
            return false;
        }
        for (int i = 0; i < count; i++)
        {
            if (!string.Equals(_pairs![i].Key, other._pairs![i].Key, StringComparison.Ordinal)
                || !_pairs[i].Value.Equals(other._pairs[i].Value))
            {
                return false;
            }
        }
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StructuredFieldParameters other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        if (_pairs is null)
        {
            return 0;
        }
        var hash = new HashCode();
        foreach (KeyValuePair<string, StructuredFieldBareItem> pair in _pairs)
        {
            hash.Add(pair.Key, StringComparer.Ordinal);
            hash.Add(pair.Value);
        }
        return hash.ToHashCode();
    }

    /// <summary>Determines whether two parameter maps are equal.</summary>
    /// <param name="left">The first map.</param>
    /// <param name="right">The second map.</param>
    /// <returns><see langword="true"/> if the maps are equal; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(StructuredFieldParameters left, StructuredFieldParameters right) => left.Equals(right);

    /// <summary>Determines whether two parameter maps are unequal.</summary>
    /// <param name="left">The first map.</param>
    /// <param name="right">The second map.</param>
    /// <returns><see langword="true"/> if the maps are unequal; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(StructuredFieldParameters left, StructuredFieldParameters right) => !left.Equals(right);
}
