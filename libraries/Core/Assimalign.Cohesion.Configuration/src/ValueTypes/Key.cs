using System;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;

[DebuggerDisplay("{ToString()}")]
[JsonConverter(typeof(KeyJsonConverter))]
public readonly struct Key : IEquatable<Key>, IComparable<Key>
{
    #region Constructors

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public Key(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            ThrowHelper.ThrowArgumentNullException(nameof(value));
        }
        if (value.ContainsAny(KeyPath.Delimiters))
        {
            ThrowHelper.ThrowArgumentException("");
        }
        if (value.Contains(LabelSeparator))
        {
            ThrowHelper.ThrowArgumentException("");
        }

        Value = value;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <param name="label"></param>
    public Key(string value, string label) : this(value)
    {
        if (string.IsNullOrEmpty(label))
        {
            ThrowHelper.ThrowArgumentNullException(nameof(label));
        }
        if (label.ContainsAny(KeyPath.Delimiters))
        {
            ThrowHelper.ThrowArgumentException("");
        }
        if (label.Contains(LabelSeparator))
        {
            ThrowHelper.ThrowArgumentException($"The parameter {label} cannot contain the '$' ");
        }

        Label = label;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The raw key value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets the label segment.
    /// </summary>
    public string? Label { get; }

    /// <summary>
    /// 
    /// </summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>
    /// The separator used to identify a labeled segment.
    /// </summary>
    public const char LabelSeparator = '$';

    #endregion

    #region Methods

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
    public bool Equals(Key other, KeyComparison comparison)
    {
        return Equals(this, other, comparison);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public static bool Equals(Key left, Key right, KeyComparison comparison)
    {
        var comparer = comparison switch
        {
            KeyComparison.Ordinal => StringComparer.Ordinal,
            KeyComparison.OrdinalIgnoreCase => StringComparer.OrdinalIgnoreCase,
            _ => throw new ArgumentException()
        };

        return comparer.Equals(left.Value, right.Value) && 
            comparer.Equals(left.Label, right.Label);
    }

    #endregion

    #region Overloads

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        // TODO: Look into using String.Create() with span allocation. May be faster.
        return string.Join(LabelSeparator, Value, Label);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public override int GetHashCode()
    {
        if (Label is not null)
        {
            return Value.GetHashCode() >> Label.GetHashCode();
        }
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
        var comparer = comparison switch
        {
            KeyComparison.Ordinal => KeyComparer.Ordinal,
            KeyComparison.OrdinalIgnoreCase => KeyComparer.OrdinalIgnoreCase,
            _ => throw new ArgumentException()
        };

        return comparer.Compare(this, other);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    public static Key Parse(string value)
    {
        return Parse(value.AsSpan());
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="segment"></param>
    /// <returns></returns>
    public static Key Parse(ReadOnlySpan<char> segment)
    {
        ReadOnlySpan<char> value = segment;
        ReadOnlySpan<char> label = ReadOnlySpan<char>.Empty;

        // Check for label `$`
        int labelIndex = segment.IndexOf('$');
        if (labelIndex != -1)
        {
            value = segment.Slice(0, labelIndex);
            label = segment.Slice(labelIndex + 1);

            return new Key(new string(value), new string(label));
        }

        return new Key(new string(value));
    }

    #endregion

    #region Operators

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    public static implicit operator string(Key key) => key.ToString();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    public static implicit operator Key(string value) => Key.Parse(value);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(Key left, Key right) => left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator !=(Key left, Key right) => !left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(Key? left, Key right) => left.HasValue && left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator !=(Key? left, Key right) => left.HasValue && !left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(Key left, Key? right) => right.HasValue && left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator !=(Key left, Key? right) => right.HasValue && !left.Equals(right);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(Key? left, Key? right) => (!left.HasValue && !right.HasValue) || (left.HasValue && right.HasValue && left.Value.Equals(right.Value));

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator !=(Key? left, Key? right) => (!left.HasValue && right.HasValue) || (left.HasValue && !right.HasValue) || (left.HasValue && right.HasValue && left.Value.Equals(right.Value));

    #endregion

    #region Partials

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

            return Key.Parse(str);
        }

        public override void Write(Utf8JsonWriter writer, Key value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
        }
    }

    #endregion
}