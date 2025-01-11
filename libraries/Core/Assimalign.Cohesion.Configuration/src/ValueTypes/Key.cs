using System;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;
using System.Runtime.CompilerServices;

/*
    - Slash Format: 			"/key1/"
	- Backward Slash Format:	"\\key1\\key2[index]\\key3"
	- Namespace Format:			"key1.key2[index].key3
	- Colon Format:				"key1:key2[index]:key3"
	- Mixed Format:				"/key1.key2\\key3[index]:key4"
    - Colon Format (labels):    "key1$label3:key2[index]:key3$label2"
 */
/// <summary>
/// The configuration <see cref="Key"/> is a representation of a composite key within a key-value pair.
/// </summary>
[DebuggerDisplay("{ToString()}")]
public readonly struct Key : IEquatable<Key>
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="segments"></param>
    public Key(KeySegment[] segments)
    {
        Segments = segments;
    }

    #region Properties/Fields
    /// <summary>
    /// Get the key parts.
    /// </summary>
    public KeySegment[] Segments { get; }
    /// <summary>
    /// Returns the default separator.
    /// </summary>
    public const char DefaultSeparator = ':';
    /// <summary>
    /// Allowed separators.
    /// </summary>
    public static readonly char[] Separators = ['\\', '/', ':', '.'];
    /// <summary>
    /// Gets an empty key.
    /// </summary>
    public static readonly Key Empty = "";
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    //public Key GetInnerMostSegment()
    //{
    //    return new(Segments[Segments.Length - 1].ToString());
    //}
    #endregion

    #region Methods
    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public Key Concat(Key other)
    {
        return Combine(this, other);
    }
    #endregion

    #region Overloads
    /// <summary>
    /// Returns a formatted key value.
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return string.Join(DefaultSeparator, [.. Segments]);
    }
    public override int GetHashCode()
    {
        return ToString().GetHashCode();
    }
    public override bool Equals(object? obj)
    {
        if (obj is Key key)
        {
            return Equals(key);
        }
        return false;
    }
    #endregion

    #region Interfaces
    public bool Equals(Key other)
    {
        return Equals(other, KeyComparison.Ordinal);
    }
    #endregion

    #region Helpers
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
    public static bool Equals(Key left, Key right, KeyComparison comparison)
    {
        var lefts = left.Segments;
        var rights = right.Segments;

        if (lefts.Length == rights.Length)
        {
            for (int i = 0; i < lefts.Length; i++)
            {
                if (!lefts[i].Equals(rights[i], comparison))
                {
                    return false;
                }
            }
            return true;
        }

        return false;
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static Key Combine(Key left, Key right)
    {
        return new Key([.. left.Segments, .. right.Segments]);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Key Create(string? value)
    {
        if (value is null)
        {
            return Key.Empty;
        }

        var key = value?.Trim(Separators);

        if (string.IsNullOrEmpty(key))
        {
            return Key.Empty;
        }

        var segments = new KeySegment[4];
        var index = 0;
        var count = 0;

        while (index < key.Length)
        {
            var next = key.IndexOfAny(Separators, index);

            if (next == -1)
            {
                // No separator found. Consume the remainder of the string.
                next = key.Length;
            }

            segments[count] = new KeySegment(key.Substring(index, next - index));
            index = next + 1;
            count++;
        }

        if (segments.Length != count)
        {
            Array.Resize(ref segments, count);
        }

        return new Key(segments);
    }
    #endregion

    #region Operators
    public static implicit operator string(Key key)
    {
        return key.ToString();
    }
    public static implicit operator Key(string value)
    {
        return Key.Create(value);
    }
    public static Key operator +(Key left, Key right)
    {
        return Combine(left, right);
    }
    public static bool operator ==(Key left, Key right)
    {
        return left.Equals(right);
    }
    public static bool operator !=(Key left, Key right)
    {
        return !left.Equals(right);
    }
    public static bool operator ==(Key? left, Key right)
    {
        return left.HasValue && left.Equals(right);
    }
    public static bool operator !=(Key? left, Key right)
    {
        return left.HasValue && !left.Equals(right);
    }
    public static bool operator ==(Key left, Key? right)
    {
        return right.HasValue && left.Equals(right);
    }
    public static bool operator !=(Key left, Key? right)
    {
        return right.HasValue && !left.Equals(right);
    }
    //public static bool operator ==(Key? left, Key? right)
    //{
    //    return right.HasValue && left.Equals(right);
    //}
    //public static bool operator !=(Key? left, Key? right)
    //{
    //    return right.HasValue && !left.Equals(right);
    //}
    #endregion
}