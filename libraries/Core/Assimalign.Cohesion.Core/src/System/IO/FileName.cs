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
    public FileName(string name)
    {
        ThrowHelper.ThrowIfNullOrEmpty(name);

        int start = 0;
        
        CalculateTrimStart(name, ref start);

        string error = null!;

        _value = string.Create(name.Length - start, name, (span, value) =>
        {
            for (int i = start; i < name.Length; i++)
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

        if (_value.Length > MaxLength)
        {
            ThrowHelper.ThrowArgumentException($"The file name is too long. Max Length allowed is {MaxLength}");
        }

        if (error is not null)
        {
            ThrowHelper.ThrowArgumentException(error);
        }
    }

    /// <summary>
    /// The maximum length of a file name.
    /// </summary>
    public const int MaxLength = 255;

    #region Methods

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
        return Equals(other, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="cultureInfo"></param>
    /// <returns></returns>
    public bool Equals(FileName other, CultureInfo cultureInfo)
    {
        return StringComparer.Create(cultureInfo, true).Equals(_value, other._value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public int CompareTo(FileName other)
    {
        return CompareTo(other, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="cultureInfo"></param>
    /// <returns></returns>
    public int CompareTo(FileName other, CultureInfo cultureInfo)
    {
        return StringComparer.Create(cultureInfo, true).Compare(_value, other._value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cultureInfo"></param>
    /// <returns></returns>
    public int GetHashCode(CultureInfo cultureInfo)
    {
        return StringComparer.Create(cultureInfo, true).GetHashCode(_value);
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
        return GetHashCode(CultureInfo.InvariantCulture);
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