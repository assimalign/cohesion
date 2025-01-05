using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Runtime.CompilerServices;
#if NET7_0_OR_GREATER
using System.Numerics;
#endif

namespace Assimalign.Cohesion;

using Internal;


/// <summary>
/// Represents the size of data.
/// </summary>
[DebuggerDisplay("Length: {Gigabytes}")]
public readonly struct Size :  IEquatable<Size> ,IComparable<Size>, IEqualityComparer<Size>
#if NET7_0_OR_GREATER
    ,IEqualityOperators<Size, Size, bool>
    ,IAdditionOperators<Size, Size, Size>
    ,ISubtractionOperators<Size, Size, Size>
#endif
{
    private const long kilobyte = 1000;
    private const long megabyte = kilobyte * 1000;
    private const long gigabyte = megabyte * 1000;
    private const long terabyte = gigabyte * 1000;
    private const long petabyte = terabyte * 1000;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="length">The length of bytes.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Size(long length)
    {
        if (length < -1)
        {
            ThrowHelper.ThrowArgumentException("File size must be greater than -1.");
        }
        Length = length;
    }

    #region Implementation
    /// <summary>
    /// Returns an empty size.
    /// </summary>
    public static Size Empty => new Size(-1);
    /// <summary>
    /// The length represented in 
    /// </summary>
    public long Length { get; }
    /// <summary>
    /// The size represented in kilobytes.
    /// </summary>
    public double Kilobytes
    {
        get
        {
            if (Length == -1)
            {
                return 0;
            }
            return ((double)Length) / kilobyte;
        }
    }
    /// <summary>
    /// The size represented in megabytes.
    /// </summary>
    public double Megabytes
    {
        get
        {
            if (Length == -1)
            {
                return 0;
            }
            return ((double)Length) / megabyte;
        }
    }
    /// <summary>
    /// The size represented in gigabytes.
    /// </summary>
    public double Gigabytes
    {
        get
        {
            if (Length == -1)
            {
                return 0;
            }
            return ((double)Length) / gigabyte;
        }
    }
    /// <summary>
    /// The size represented in terabytes.
    /// </summary>
    public double Terabytes
    {
        get
        {
            if (Length == -1)
            {
                return 0;
            }
            return ((double)Length) / terabyte;
        }
    }
    /// <summary>
    /// The size represented in petabytes.
    /// </summary>
    public double Petabytes
    {
        get
        {
            if (Length == -1)
            {
                return 0;
            }
            return ((double)Length) / petabyte;
        }
    }
    #endregion

    #region Overloads

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is Size size ? Equals(size) : false;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return ToString("b");
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(typeof(Size), Length);
    }
    #endregion
    
    #region Interfaces
    public bool Equals(Size other)
    {
        return other.Length == Length;
    }
    public bool Equals(Size left, Size right)
    {
        return left.Equals(right);
    }
    public int GetHashCode([DisallowNull] Size obj)
    {
        return obj.GetHashCode();
    }
    public int CompareTo(Size other)
    {
        return Length.CompareTo(other.Length);
    }
    /// <summary>
    /// Returns a string representation of the size in measurement length.
    /// Valid formats: b, kb, mb, gb, tb, pb
    /// </summary>
    /// <param name="format"></param>
    /// <returns></returns>
    public string ToString(string? format)
    {
        format ??= "b";

        return format switch
        {
            "b" => Length.ToString(),
            "kb" => Kilobytes.ToString(),
            "mb" => Megabytes.ToString(),
            "gb" => Gigabytes.ToString(),
            "tb" => Terabytes.ToString(),
            "pb" => Petabytes.ToString(),
            _ => Length.ToString()
        };
    }
    #endregion

    #region Operators
    public static implicit operator long(Size fileSize)
    {
        return fileSize.Length;
    }
    public static implicit operator Size(long length)
    {
        return new Size(length);
    }
    public static bool operator ==(Size left, Size right)
    {
        return left.Equals(right);
    }
    public static bool operator !=(Size left, Size right)
    {
        return !left.Equals(right);
    }
    public static bool operator >(Size left, Size right)
    {
        return left.CompareTo(right) > 0;
    }
    public static bool operator <(Size left, Size right)
    {
        return left.CompareTo(right) < 0;
    }
    public static bool operator >=(Size left, Size right)
    {
        return left.CompareTo(right) >= 0;
    }
    public static bool operator <=(Size left, Size right)
    {
        return left.CompareTo(right) <= 0;
    }
    public static Size operator +(Size left, Size right)
    {
        return left.Length + right.Length;
    }
    public static Size operator -(Size left, Size right)
    {
        var value = left.Length - right.Length;
        if (value < -1)
        {

        }
        return value;
    }
    #endregion

    #region Helpers
    public static Size FromKilobytes(double size)
    {
        return new Size((long)(size * kilobyte));
    }
    public static Size FromMegabytes(double size)
    {
        return new Size((long)(size * megabyte));
    }
    public static Size FromGigabytes(double size)
    {
        return new Size((long)(size * gigabyte));
    }
    public static Size FromTerabytes(double size)
    {
        return new Size((long)(size * terabyte));
    }
    public static Size FromPetabytes(double size)
    {
        return new Size((long)(size * terabyte));
    }
    #endregion
}