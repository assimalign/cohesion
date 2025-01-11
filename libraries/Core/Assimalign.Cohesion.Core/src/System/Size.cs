using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Runtime.CompilerServices;
#if NET7_0_OR_GREATER
using System.Numerics;
#endif

namespace System;

using Assimalign.Cohesion.Internal;

/// <summary>
/// Represents the size of data.
/// </summary>
[DebuggerDisplay("Length: {Gigabytes}")]
public readonly struct Size : IEquatable<Size>, IComparable<Size>, IEqualityComparer<Size>
#if NET7_0_OR_GREATER
    , IEqualityOperators<Size, Size, bool>
    , IAdditionOperators<Size, Size, Size>
    , ISubtractionOperators<Size, Size, Size>
#endif
{
    /// <summary>
    /// The default constructor
    /// </summary>
    /// <param name="length">The number bytes.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Size(long length)
    {
        if (length < -1)
        {
            ThrowHelper.ThrowArgumentException($"The '{nameof(length)}' must be greater than -1.");
        }
        Length = length;
    }

    #region Implementation
    /// <summary>
    /// Returns an empty size.
    /// </summary>
    public static Size Empty => new Size(-1);
    /// <summary>
    /// The number of 
    /// </summary>
    public long Bits => Length * 8;
    /// <summary>
    /// The length represented in 
    /// </summary>
    public long Length { get; }

    // Decimal Prefix

    /// <summary>
    /// The size represented in kilobytes.
    /// </summary>
    public double Kilobytes => Calculate(1000, 1);
    /// <summary>
    /// The size represented in megabytes.
    /// </summary>
    public double Megabytes => Calculate(1000, 2);
    /// <summary>
    /// The size represented in gigabytes.
    /// </summary>
    public double Gigabytes => Calculate(1000, 3);
    /// <summary>
    /// The size represented in terabytes.
    /// </summary>
    public double Terabytes => Calculate(1000, 4);
    /// <summary>
    /// The size represented in petabytes.
    /// </summary>
    public double Petabytes => Calculate(1000, 5);

    // Binary Prefix

    /// <summary>
    /// The size represented in kibibytes.
    /// </summary>
    public double Kibibytes => Calculate(1024, 1);
    /// <summary>
    /// The size represented in mebibytes.
    /// </summary>
    public double Mebibytes => Calculate(1024, 2);
    /// <summary>
    /// The size represented in gibibytes.
    /// </summary>
    public double Gibibytes => Calculate(1024, 3);
    /// <summary>
    /// The size represented in tebibytes.
    /// </summary>
    public double Tebibytes => Calculate(1024, 4);
    /// <summary>
    /// The size represented in pebibytes. 
    /// </summary>
    public double Pebibytes => Calculate(1024, 5);

    private double Calculate(int value, int unit)
    {
        return Length == -1 ?
            0 :
            (double)Length / Math.Pow(value, unit);
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
        return Length.GetHashCode();
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
    /// Valid formats: b, kb, ki, mb, mi, gb, gi, tb, ti, pb, pi
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
            "ki" => Kibibytes.ToString(),
            "mi" => Mebibytes.ToString(),
            "gi" => Gibibytes.ToString(),
            "ti" => Tebibytes.ToString(),
            "pi" => Pebibytes.ToString(),
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
    public static implicit operator Size(int length)
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
    /// <summary>
    /// 
    /// </summary>
    /// <param name="size">The number of kilobytes.</param>
    /// <returns></returns>
    public static Size FromKilobytes(double size)
    {
        return new Size((long)(size * Math.Pow(1000, 1)));
    }
    public static Size FromKibibytes(double size)
    {
        return new Size((long)(size * Math.Pow(1024, 1)));
    }
    public static Size FromMegabytes(double size)
    {
        return new Size((long)(size * Math.Pow(1000, 2)));
    }
    public static Size FromMebibytes(double size)
    {
        return new Size((long)(size * Math.Pow(1024, 2)));
    }
    public static Size FromGigabytes(double size)
    {
        return new Size((long)(size * Math.Pow(1000, 3)));
    }
    public static Size FromGibibytes(double size)
    {
        return new Size((long)(size * Math.Pow(1024, 3)));
    }
    public static Size FromTerabytes(double size)
    {
        return new Size((long)(size * Math.Pow(1000, 4)));
    }
    public static Size FromTebibytes(double size)
    {
        return new Size((long)(size * Math.Pow(1024, 4)));
    }
    public static Size FromPetabytes(double size)
    {
        return new Size((long)(size * Math.Pow(1000, 5)));
    }
    public static Size FromPebibytes(double size)
    {
        return new Size((long)(size * Math.Pow(1000, 5)));
    }
    #endregion
}