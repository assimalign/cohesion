using System.Linq;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Collections.Generic;
#if NET7_0_OR_GREATER
using System.Numerics;
#endif

namespace System.IO;

using Assimalign.Cohesion.Internal;
using static Assimalign.Cohesion.Internal.PathHelper;

/// <summary>
/// A case insensitive directory name.
/// </summary>
[DebuggerDisplay("{_value}")]
public readonly struct DirectoryName : IEquatable<DirectoryName>, IComparable<DirectoryName>
#if NET7_0_OR_GREATER
    ,IEqualityOperators<DirectoryName, DirectoryName, bool>
#endif
{
    private readonly string _value;

    public DirectoryName(string value)
    {
        ThrowHelper.ThrowIfNullOrEmpty(value, nameof(value));

        if (value.Length == 1 && IsPathSeparator(value[0]))
        {
            _value = "/";
            return;
        }

        string error = null!;

        int start = 0;
        int end = value.Length - 1;

        CalculateTrimRange(value, ref start, ref end);

        _value = string.Create((end + 2) - start, value, (span, value) =>
        {
            for (int i = start; i < (end + 1); i++)
            {
                var current = value[i];

                if (!IsValidNameChar(current))
                {
                    error = $"The directory name has an invalid character `{current}`.";
                    break;
                }

                span[i - start] = current;
            }
            // ending slash's indicate directory so lets concat each name with an ending slash
            span[span.Length - 1] = '/';
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
    /// The max directory name length.
    /// </summary>
    public const int MaxLength = 255;

    /// <summary>
    /// 
    /// </summary>
    public bool IsRoot => _value[0] == '/';

    /// <summary>
    /// Returns an empty directory name.
    /// </summary>
    public static DirectoryName Root { get; } = "/";

    #region Methods

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public bool Equals(DirectoryName other)
    {
        return Equals(other, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="cultureInfo"></param>
    /// <returns></returns>
    public bool Equals(DirectoryName other, CultureInfo cultureInfo)
    {
        return StringComparer.Create(cultureInfo, true).Equals(_value, other._value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public int CompareTo(DirectoryName other)
    {
        return CompareTo(other, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <param name="cultureInfo"></param>
    /// <returns></returns>
    public int CompareTo(DirectoryName other, CultureInfo cultureInfo)
    {
        return StringComparer.Create(cultureInfo, true).Compare(_value, other._value);
    }

    public int GetHashCode(CultureInfo cultureInfo)
    {
        return StringComparer.Create(cultureInfo, true).GetHashCode(_value);
    }

    #region Overloads
    public override bool Equals([NotNullWhen(true)] object? obj)
    {
        if (obj is null)
        {
            return false;
        }
        if (obj is not DirectoryName name)
        {
            return false;
        }
        return Equals(name);
    }

    public override string ToString()
    {
        return _value;
    }

    public override int GetHashCode()
    {
        return GetHashCode(CultureInfo.InvariantCulture);
    }

    #endregion

    #endregion

    #region Operators

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    public static implicit operator DirectoryName(string value)
    {
        return new DirectoryName(value);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="name"></param>
    public static implicit operator string(DirectoryName name)
    {
        return name._value;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="name"></param>
    public static implicit operator FileSystemPath(DirectoryName name)
    {
        return name._value;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(DirectoryName left, DirectoryName right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator !=(DirectoryName left, DirectoryName right)
    {
        return !left.Equals(right);
    }

    #endregion
}
