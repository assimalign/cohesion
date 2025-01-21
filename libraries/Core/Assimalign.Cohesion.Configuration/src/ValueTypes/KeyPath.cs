using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// The configuration <see cref="KeyPath"/> is a representation of a composite key within a key-value pair. 
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
///     <description>"/key1.key2\\key3[index]:key4"</description>
/// </item>
/// <item>
///     <term>Colon Format (labels)</term>
///     <description>"key1$label1:key2$label2[index]:key3$label3"</description>
/// </item>
/// </list>
/// </summary>
[DebuggerDisplay("{ToString()}")]
[JsonConverter(typeof(KeyPathJsonConvertor))]
public readonly struct KeyPath : IEquatable<KeyPath>, IEnumerable<Key>
{
    #region Constructors

    /// <summary>
    /// The default constructor
    /// </summary>
    /// <param name="keys"></param>
    public KeyPath(Key[] keys)
    {
        Keys = (keys ??= []);
    }

    #endregion

    #region Properties

    /// <summary>
    /// Returns the default separator used within a composite key.
    /// </summary>
    public const char DefaultDelimiter = ':';

    /// <summary>
    /// Allowed delimiters.
    /// </summary>
    public static readonly char[] Delimiters = ['\\', '/', ':', '.'];

    /// <summary>
    /// Gets an empty key.
    /// </summary>
    public static readonly KeyPath Empty = "";

    /// <summary>
    /// The collection of keys that make up the path.
    /// </summary>
    public Key[] Keys { get; } = [];

    /// <summary>
    /// The number of keys in the path.
    /// </summary>
    public int Count => Keys.Length;

    /// <summary>
    /// Checks whether the path has any keys.
    /// </summary>
    public bool IsEmpty => Keys.Length == 0;

    #endregion

    #region Methods

    /// <summary>
    /// Gets the last key in the path.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="IndexOutOfRangeException"></exception>
    public Key GetLastKey() => Keys[Keys.Length - 1];

    /// <summary>
    /// Gets the first key in the path.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="IndexOutOfRangeException"></exception>
    public Key GetFirstKey() => Keys[0];

    /// <summary>
    /// 
    /// </summary>
    /// <param name="index"></param>
    /// <returns></returns>
    public Key GetKey(int index)
    {
        return Keys[index];
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="start"></param>
    /// <returns></returns>
    public KeyPath GetSubpath(int start)
    {
        return GetSubpath(start, Keys.Length - start);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="start">The starting index to begin.</param>
    /// <param name="length"></param>
    /// <returns></returns>
    public KeyPath GetSubpath(int start, int length)
    {
        var buffer = new Key[length];

        Keys.CopyTo(buffer, start);

        return new KeyPath(buffer);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public KeyPath Combine(KeyPath other)
    {
        return Combine(this, other);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static KeyPath Combine(KeyPath left, KeyPath right)
    {
        return new KeyPath([.. left.Keys, .. right.Keys]);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public bool Equals(KeyPath other)
    {
        return Equals(other, KeyComparison.Ordinal);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public bool Equals(KeyPath other, KeyComparison comparison)
    {
        if (Count != other.Count)
        {
            return false;
        }

        for (int i = 0; i < Count; i++)
        {
            var left = Keys[i];
            var right = other.Keys[i];

            if (!left.Equals(right, comparison))
            {
                return false;
            }
        }

        return true;
    }
    public IEnumerator<Key> GetEnumerator()
    {
        return (IEnumerator<Key>)Keys.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    #endregion

    #region Overloads

    /// <summary>
    /// Formats the key path with the path's default delimiter.
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return string.Join(DefaultDelimiter, Keys);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="obj"></param>
    /// <returns></returns>
    public override bool Equals([NotNullWhen(true)] object? obj)
    {
        return obj is KeyPath path ? Equals(path) : false;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public override int GetHashCode()
    {
        return ToString().GetHashCode();
    }

    #endregion

    #region Helpers

    //[MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static KeyPath Parse(string? value)
    {
        return Parse(value.AsSpan());
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="span"></param>
    /// <returns></returns>
    public static KeyPath Parse(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty)
        {
            return new KeyPath([]);
        }

        int start = 0;
        int count = 0;
        Key[] segments = new Key[5];

        while (start < span.Length)
        {
            // Find the next segment by locating ':'
            int segmentEnd = span.Slice(start).IndexOfAny(Delimiters);

            if (segmentEnd == -1)
            {
                segmentEnd = span.Length - start;

                Array.Resize(ref segments, count + 1);
            }

            ReadOnlySpan<char> segment = span.Slice(start, segmentEnd);

            start += segmentEnd + 1; // Move start past the current segment

            // Parse the segment
            segments[count] = Key.Parse(segment);

            count++;

            if (count > segments.Length)
            {
                Array.Resize(ref segments, count + 5);
            }
        }

        return new KeyPath(segments);
    }

    #endregion

    #region Operators
    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    public static implicit operator string(KeyPath path) => path.ToString();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    public static implicit operator KeyPath(string value) => Parse(value);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    public static implicit operator KeyPath(Key key) => new KeyPath([key]);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="keys"></param>
    public static implicit operator KeyPath(Key[] keys) => new KeyPath(keys);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static KeyPath operator +(KeyPath left, KeyPath right) => Combine(left, right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(KeyPath left, KeyPath right) => left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator !=(KeyPath left, KeyPath right) => !left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(KeyPath? left, KeyPath right) => left.HasValue && left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator !=(KeyPath? left, KeyPath right) => left.HasValue && !left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    // public static bool operator ==(KeyPath left, KeyPath? right) => right.HasValue && left.Equals(right);

    #endregion


    #region Partials

    partial class KeyPathJsonConvertor : JsonConverter<KeyPath>
    {
        public override KeyPath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException(
                    $"Unable to parse {nameof(KeyPath)}. The expected token type is string, but current type is {reader.TokenType}");
            }

            var value = reader.GetString();

            return KeyPath.Parse(value);
        }

        public override void Write(Utf8JsonWriter writer, KeyPath value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(value);
        }
    }

    #endregion
}
