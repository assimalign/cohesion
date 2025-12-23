using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Configuration;


[DebuggerDisplay("{ToString()}")]
[JsonConverter(typeof(KeyJsonConverter))]
public readonly struct Key : IEquatable<Key>, IComparable<Key>
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="span"></param>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public Key(ReadOnlySpan<char> span)
    {
        ArgumentException.ThrowIf(
            span.ContainsAny(Path.Delimiters),
            $"Key value cannot have any path delimiters: {string.Join(',', [.. Path.Delimiters])}");

        Value = new string(span);
    }

    /// <summary>
    /// The raw key value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Checks whether the key is empty.
    /// </summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public ReadOnlySpan<char> AsSpan()
    {
        return Value.AsSpan();
    }

    /// <summary>
    /// Checks whether the key value is of index.'[int]'
    /// </summary>
    /// <param name="index"></param>
    /// <returns></returns>
    /// <exception cref="FormatException" />
    //public bool IsIndexed(out int index)
    //{
    //    index = default;

    //    if (IsEmpty)
    //    {
    //        return false;
    //    }

    //    int start;
    //    int end;

    //    var span = Value.AsSpan();

    //    if ((start = span.IndexOf('[')) == -1 || (end = span.IndexOf(']')) == -1 || start > end || end != span.Length - 1)
    //    {
    //        return false;
    //    }

    //    var value = span.Slice(start + 1, end - start - 1);

    //    index = int.Parse(value);

    //    return true;
    //}

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public bool StartsWith(Key other)
    {
        return StartsWith(other, KeyComparison.Ordinal);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public bool StartsWith(Key other, KeyComparison comparison)
    {
        if (other.Value.Length > Value.Length)
        {
            return false;
        }

        return Value.StartsWith(other.Value, (StringComparison)comparison);
    }

    public bool EndsWith(Key other)
    {
        return EndsWith(other, KeyComparison.Ordinal);
    }

    public bool EndsWith(Key other, KeyComparison comparison)
    {
        if (other.Value.Length > Value.Length)
        {
            return false;
        }
        return Value.EndsWith(other.Value, (StringComparison)comparison);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public bool Equals(Key other)
    {
        return Equals(this, other, KeyComparison.Ordinal);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public bool Equals(in Key other, KeyComparison comparison)
    {
        return Equals(in this, in other, comparison);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public static bool Equals(in Key left, in Key right, KeyComparison comparison)
    {
        return KeyComparer.FromComparison(comparison).Equals(in left, in right);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return Value;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public override int GetHashCode()
    {
        return Value.GetHashCode();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="obj"></param>
    /// <returns></returns>
    public override bool Equals(object? obj)
    {
        if (obj is Key key)
        {
            return Equals(key);
        }
        return false;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public int CompareTo(Key other)
    {
        return CompareTo(other, KeyComparison.Ordinal);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public int CompareTo(Key other, KeyComparison comparison)
    {
        return KeyComparer.FromComparison(comparison).Compare(this, other);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    public static implicit operator string(in Key key) => key.ToString();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    public static implicit operator ReadOnlySpan<char>(in Key key) => key.AsSpan();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    public static implicit operator Key(string value) => new Key(value);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(in Key left, in Key right) => left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator !=(in Key left, in Key right) => !left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(in Key? left, in Key right) => left.HasValue && left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator !=(in Key? left, in Key right) => left.HasValue && !left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(in Key left, in Key? right) => right.HasValue && left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator !=(in Key left, in Key? right) => right.HasValue && !left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(in Key? left, in Key? right) => (!left.HasValue && !right.HasValue) || (left.HasValue && right.HasValue && left.Value.Equals(right.Value));

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator !=(in Key? left, in Key? right) => (!left.HasValue && right.HasValue) || (left.HasValue && !right.HasValue) || (left.HasValue && right.HasValue && left.Value.Equals(right.Value));


    partial class KeyJsonConverter : JsonConverter<Key>
    {
        public override Key Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Key expected a string token type.");
            }

            var str = reader.GetString();

            if (str is null || str == string.Empty)
            {
                throw new JsonException("Key expected a string token type.");
            }

            return new Key(str);
        }

        public override void Write(Utf8JsonWriter writer, Key value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(value);
        }
    }
}