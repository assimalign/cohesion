using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
#if NET7_0_OR_GREATER
using System.Numerics;
#endif

namespace System.IO;

using Assimalign.Cohesion.Internal;
using static Assimalign.Cohesion.Internal.PathHelper;

/// <summary>
/// A case insensitive file name.
/// </summary>
[DebuggerDisplay("{_value}")]
public readonly struct FileName : IEquatable<FileName>, IComparable<FileName>
#if NET7_0_OR_GREATER
    ,IEqualityOperators<FileName, FileName, bool>
#endif
{
    private readonly string _value;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="name"></param>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public FileName(ReadOnlySpan<char> name)
    {
        ArgumentNullException.ThrowIfEmptySpan(name);

        int start = 0;
        
        CalculateTrimStart(name, ref start);

        string error = null!;

        _value = string.Create(name.Length - start, name, (span, value) =>
        {
            for (int i = start; i < value.Length; i++)
            {
                var current = value[i];

                if (!IsValidNameChar(current))
                {
                    error = $"The file name has an invalid character `{current}`.";
                    break;
                }

                span[i - start] = current;
            }
        });

        ArgumentException.ThrowIf(_value.Length > MaxLength, $"The file name is too long. Max Length allowed is {MaxLength}");
        ArgumentException.ThrowIf(error is not null, error);
    }

    /// <summary>
    /// The maximum length of a file name.
    /// </summary>
    public const int MaxLength = 255;

    #region Methods

    /// <summary>
    /// Returns the value of the current instance as a read-only span of characters.
    /// </summary>
    /// <returns>A read-only span of characters representing the value of the current instance.</returns>
    public ReadOnlySpan<char> AsSpan()
    {
        return _value.AsSpan();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="extension"></param>
    /// <returns></returns>
    public bool HasExtension(out string extension)
    {
        return (extension = Path.GetExtension(_value)!) is not null;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public bool Equals(FileName other)
    {
        return Equals(other, StringComparison.InvariantCulture);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public bool Equals(FileName other, StringComparison comparison)
    {
        return StringComparer.FromComparison(comparison).Equals(_value, other._value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public int CompareTo(FileName other)
    {
        return CompareTo(other, StringComparison.InvariantCulture);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public int CompareTo(FileName other, StringComparison comparison)
    {
        return StringComparer.FromComparison(comparison).Compare(_value, other._value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public int GetHashCode(StringComparison comparison)
    {
        return StringComparer.FromComparison(comparison).GetHashCode(_value);
    }

    #region Overloads

    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj)
    {
        if (obj is null)
        {
            return false;
        }
        if (obj is not FileName name)
        {
            return false;
        }
        return Equals(name);
    }

    // <inheritdoc />
    public override string ToString()
    {
        return _value;
    }

    // <inheritdoc />
    public override int GetHashCode()
    {
        return GetHashCode(StringComparison.InvariantCulture);
    }

    #endregion

    #endregion

    #region Operators


    public static implicit operator FileName(string value)
    {
        return new FileName(value);
    }

    /// <summary>
    /// Returns the normalized string
    /// </summary>
    /// <param name="name"></param>
    public static implicit operator string(FileName name)
    {
        return name._value;
    }

    /// <summary>
    /// Returns a name as a path.
    /// </summary>
    /// <param name="name"></param>
    public static implicit operator FileSystemPath(FileName name)
    {
        return name._value;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(FileName left, FileName right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator !=(FileName left, FileName right)
    {
        return !left.Equals(right);
    }

    #endregion
}