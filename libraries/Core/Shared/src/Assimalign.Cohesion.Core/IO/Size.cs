using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;

namespace Assimalign.Cohesion;

using Internal;

/// <summary>
/// Represents the size of data.
/// </summary>
[DebuggerDisplay("Length: {Length} | {Gigabytes} GB")]
public readonly struct Size : IEquatable<Size>, IComparable<Size>, IEqualityComparer<Size>
{
    private const long kilobyte = 1000;
    private const long megabyte = kilobyte * 1000;
    private const long gigabyte = megabyte * 1000;
    private const long terabyte = gigabyte * 1000;
    private const long petabyte = terabyte * 1000;

    public Size(long length)
    {
        if (Length < -1)
        {
            ThrowHelper.ThrowArgumentException("File size must be greater than -1.");
        }
        Length = length;
    }
    /// <summary>
    /// Returns an empty size.
    /// </summary>
    public static Size Empty => new Size(-1);
    public long Length { get; }
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
   
    #region Overloads

    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj)
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
    public static implicit operator long(Size fileSize) => fileSize.Length;

    public static implicit operator Size(long length) => new Size(length);
    public static bool operator ==(Size left, Size right) => left.Equals(right);
    public static bool operator !=(Size left, Size right) => !left.Equals(right);
    public static bool operator >(Size left, Size right) => left.CompareTo(right) > 0;
    public static bool operator <(Size left, Size right) => left.CompareTo(right) < 0;
    public static bool operator >=(Size left, Size right) => left.CompareTo(right) >= 0;
    public static bool operator <=(Size left, Size right) => left.CompareTo(right) <= 0;
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