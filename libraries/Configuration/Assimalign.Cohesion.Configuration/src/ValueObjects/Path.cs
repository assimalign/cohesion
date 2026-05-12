using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// The configuration <see cref="Path"/> is a representation of a composite key within a key-value pair. 
/// <para>Format Examples:</para>
/// <list type="bullet">
/// <item>
///     <term>Slash Format</term>
///     <description>"/key1/"</description>
/// </item>
/// <item>
///     <term>Backward Slash Format</term>
///     <description>"\\key1\\key2[index]\\key3"</description>
/// </item>
/// <item>
///     <term>Namespace Format</term>
///     <description>"key1.key2[index].key3</description>
/// </item>
/// <item>
///     <term>Colon Format</term>
///     <description>"key1:key2[index]:key3"</description>
/// </item>
/// <item>
///     <term>Mixed Format</term>
///     <description>"/key1.key2\\key3[2]:key4"</description>
/// </item>
/// <item>
///     <term>Colon Format (labels)</term>
///     <description>"key1$label1:key2$label2[index]:key3$label3"</description>
/// </item>
/// </list>
/// </summary>
[DebuggerDisplay("{ToString()}")]
[JsonConverter(typeof(KeyPathJsonConvertor))]
public readonly struct Path : IEquatable<Path>, IEnumerable<Key>
{
    private static readonly Key[] _emptyKeys = [];
    private readonly Key[] _keys;

    /// <summary>
    /// The default constructor
    /// </summary>
    /// <param name="keys"></param>
    public Path(Key[] keys)
    {
        _keys = keys is null || keys.Length == 0
            ? _emptyKeys
            : keys;
    }

    /// <summary>
    /// Gets a key at the provided <paramref name="index"/>.
    /// </summary>
    /// <param name="index"></param>
    /// <returns></returns>
    ///<exception cref="IndexOutOfRangeException"></exception>
    public Key this[int index]
    {
        get
        {
            ref Key key = ref Keys[index];

            return key;
        }
    }

    /// <summary>
    /// Returns the default separator used within a composite key.
    /// </summary>
    public const char DefaultDelimiter = ':';

    /// <summary>
    /// Allowed delimiters.
    /// </summary>
    public static ReadOnlySpan<char> Delimiters => ['\\', '/', ':', '.'];

    /// <summary>
    /// Gets an empty key.
    /// </summary>
    public static readonly Path Empty = new(_emptyKeys);

    /// <summary>
    /// The collection of keys that make up the path.
    /// </summary>
    public Key[] Keys => _keys ?? _emptyKeys;

    /// <summary>
    /// The number of keys in the path.
    /// </summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _keys?.Length ?? 0;
    }

    /// <summary>
    /// Checks whether the path has any keys.
    /// </summary>
    public bool IsEmpty => Count == 0;

    /// <summary>
    /// Checks if the path is made up of two or more keys.
    /// </summary>
    public bool IsComposite => Count > 1;

    /// <summary>
    /// Creates a subpath at the 
    /// </summary>
    /// <param name="start"></param>
    /// <returns></returns>
    public Path Subpath(int start)
    {
        return Subpath(start, Count - start);
    }

    /// <summary>
    /// Creates a subpath at a given index for a given length.
    /// </summary>
    /// <param name="start">The starting index to begin.</param>
    /// <param name="length">Then number os keys from the start to include</param>
    /// <returns></returns>
    public Path Subpath(int start, int length)
    {
        return Keys[start..(start + length)];
    }

    /// <summary>
    /// Combines the current instance with the provided path.
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public Path Combine(Path other)
    {
        return Combine(this, other);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public Path Combine(Path other, KeyComparison comparison)
    {
        return Combine(this, other, comparison);
    }

    /// <summary>
    /// Combines the two paths into one.
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static Path Combine(Path left, Path right)
    {
        return Combine(left, right, KeyComparison.Ordinal);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public static Path Combine(Path left, Path right, KeyComparison comparison)
    {
        if (left.IsEmpty)
        {
            return right;
        }

        if (right.IsEmpty)
        {
            return left;
        }

        if (right.StartsWith(left, comparison))
        {
            return right;
        }

        return new Path([.. left.Keys, .. right.Keys]);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public bool StartsWith(in Path other)
    {
        return StartsWith(other, KeyComparison.Ordinal);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public bool StartsWith(in Path other, KeyComparison comparison)
    {
        if (other.IsEmpty)
        {
            return true;
        }

        // Has more keys than the current instance than it cannot be a match.
        if (other.Count > Count)
        {
            return false;
        }

        Key[] keys = _keys ?? _emptyKeys;
        Key[] otherKeys = other._keys ?? _emptyKeys;

        // Get the index of the last key
        int last = other.Count - 1;

        for (int i = 0; i < last; i++)
        {
            if (!keys[i].Equals(otherKeys[i], comparison))
            {
                return false;
            }
        }

        return keys[last].StartsWith(otherKeys[last], comparison);
    }

    /// <summary>
    /// Does a
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public bool Equals(Path other)
    {
        return Equals(other, KeyComparison.Ordinal);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public bool Equals(Path other, KeyComparison comparison)
    {
        if (Count != other.Count)
        {
            return false;
        }

        Key[] keys = _keys ?? _emptyKeys;
        Key[] otherKeys = other._keys ?? _emptyKeys;

        for (int i = 0; i < Count; i++)
        {
            ref Key left = ref keys[i];
            ref Key right = ref otherKeys[i];

            if (!left.Equals(right, comparison))
            {
                return false;
            }
        }

        return true;
    }

    //public ReadOnlySpan<char> AsSpan()
    //{
    //    return new ReadOnlySpan<char>(ref )
    //    return ToString().AsSpan();
    //}

    public IEnumerator<Key> GetEnumerator()
    {
        return ((IEnumerable<Key>)Keys).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <summary>
    /// Formats the key path with the path's default delimiter.
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return IsEmpty
            ? string.Empty
            : string.Join(DefaultDelimiter, Keys);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="obj"></param>
    /// <returns></returns>
    public override bool Equals([NotNullWhen(true)] object? obj)
    {
        return obj is Path path ? Equals(path) : false;
    }

    /// <summary>
    /// Returns a hash code using the specified key comparison.
    /// </summary>
    /// <param name="comparison">The comparison used to hash each key in the path.</param>
    /// <returns>The hash code for this path.</returns>
    public int GetHashCode(KeyComparison comparison)
    {
        ArgumentException.ThrowIfEnumNotDefined(comparison);

        Key[] keys = _keys ?? _emptyKeys;
        int hashCode = keys.Length;

        unchecked
        {
            for (int i = 0; i < keys.Length; i++)
            {
                hashCode = (hashCode * 397) ^ keys[i].GetHashCode(comparison);
            }
        }

        return hashCode;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public override int GetHashCode()
    {
        return GetHashCode(KeyComparison.Ordinal);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    public static Path Parse(string? value)
    {
        return Parse(value.AsSpan());
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="span"></param>
    /// <returns></returns>
    public static Path Parse(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty)
        {
            return Empty;
        }

        int start = 0;
        int end = span.Length - 1;

        while (start <= end && IsDelimiter(span[start]))
        {
            start++;
        }

        while (end >= start && IsDelimiter(span[end]))
        {
            end--;
        }

        if (start > end)
        {
            return Empty;
        }

        int count = 0;
        Key[] segments = new Key[5];
        ReadOnlySpan<char> trimmed = span.Slice(start, end - start + 1);

        start = 0;

        while (start < trimmed.Length)
        {
            int segmentEnd = trimmed.Slice(start).IndexOfAny(Delimiters);

            if (segmentEnd == -1)
            {
                segmentEnd = trimmed.Length - start;
            }

            if (count == segments.Length)
            {
                Array.Resize(ref segments, count + 5);
            }

            segments[count] = new Key(trimmed.Slice(start, segmentEnd));

            count++;
            start += segmentEnd + 1;
        }

        if (count != segments.Length)
        {
            Array.Resize(ref segments, count);
        }

        return new Path(segments);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    public static implicit operator string(in Path path) => path.ToString();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    public static implicit operator Path(string value) => Parse(value);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    public static implicit operator Path(Key key) => new Path([key]);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="keys"></param>
    public static implicit operator Path(Key[] keys) => new Path(keys);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    public static implicit operator Key[](Path path) => path.Keys;

    /// <summary>
    /// Combines two paths together.
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static Path operator +(Path left, Path right) => Combine(left, right);

    /// <summary>
    /// Decrements the path from the root.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public static Path operator --(Path path) => path.Subpath(1, path.Count - 1);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static Path operator +(Path left, Key right) => Combine(left, right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static Path operator +(Key left, Path right) => Combine(left, right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(Path left, Path right) => left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator !=(Path left, Path right) => !left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(Path? left, Path right) => left.HasValue && left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator !=(Path? left, Path right) => left.HasValue && !left.Equals(right);

    private static bool IsDelimiter(char value)
    {
        return value is '\\' or '/' or ':' or '.';
    }

    partial class KeyPathJsonConvertor : JsonConverter<Path>
    {
        public override Path Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException(
                    $"Unable to parse {nameof(Path)}. The expected token type is string, but current type is {reader.TokenType}");
            }

            var value = reader.GetString();

            return Path.Parse(value);
        }
        public override void Write(Utf8JsonWriter writer, Path value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(value);
        }
    }
}
