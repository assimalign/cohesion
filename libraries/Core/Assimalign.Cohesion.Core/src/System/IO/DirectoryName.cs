using System.Linq;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
#if NET7_0_OR_GREATER
using System.Numerics;
#endif

namespace System.IO;

using Assimalign.Cohesion.Internal;

/// <summary>
/// 
/// </summary>
[DebuggerDisplay("{Value}")]
public readonly struct DirectoryName :
    IEquatable<DirectoryName>,
    IEqualityComparer<DirectoryName>,
    IComparable<DirectoryName>
#if NET7_0_OR_GREATER
    ,IEqualityOperators<DirectoryName, DirectoryName, bool>
#endif
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    public DirectoryName(string value)
    {
        var name = (value ?? string.Empty).Trim('\\', '/');

        if (name.ContainsAny(Path.GetInvalidFileNameChars(), out var invalid))
        {
            ThrowHelper.ThrowArgumentException($"The directory name has an invalid character `{invalid}`.");
        }

        Value = name;
    }

   

    /// <summary>
    /// The raw name value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// 
    /// </summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>
    /// Returns an empty directory name.
    /// </summary>
    public static DirectoryName Empty { get; } = "";

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
        return Value;
    }

    public override int GetHashCode()
    {
        return Value.GetHashCode();
    }

    #endregion

    #region Methods

    public int CompareTo(DirectoryName other)
    {
        return CompareTo(this, other, StringComparison.Ordinal);
    }

    public bool Equals(DirectoryName other)
    {
        return Equals(this, other, StringComparison.Ordinal);
    }

    public bool Equals(DirectoryName other, StringComparison comparison)
    {
        return Equals(this, other, comparison);
    }

    public bool Equals(DirectoryName left, DirectoryName right)
    {
        return Equals(left, right, StringComparison.Ordinal);
    }

    public int GetHashCode([DisallowNull] DirectoryName obj)
    {
        return obj.GetHashCode();
    }


    public static bool Equals(DirectoryName left, DirectoryName right, StringComparison comparison)
    {
        return StringComparer.FromComparison(comparison).Equals(left.Value, right.Value);
    }

    public static int CompareTo(DirectoryName left, DirectoryName right, StringComparison comparison)
    {
        return StringComparer.FromComparison(comparison).Compare(left.Value, right.Value);
    }

    #endregion

    #region Operators


    public static implicit operator DirectoryName(string value)
    {
        return new DirectoryName(value);
    }

    public static implicit operator string(DirectoryName name)
    {
        return name.Value;
    }
    public static implicit operator FileSystemPath(DirectoryName name)
    {
        return new FileSystemPath("/" + name.Value);
    }

    public static bool operator ==(DirectoryName left, DirectoryName right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(DirectoryName left, DirectoryName right)
    {
        return left.Equals(right);
    }

    #endregion
}
