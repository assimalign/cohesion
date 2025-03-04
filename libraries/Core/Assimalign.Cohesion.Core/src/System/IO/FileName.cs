using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
#if NET7_0_OR_GREATER
using System.Numerics;
#endif

namespace System.IO;

using Assimalign.Cohesion.Internal;

/// <summary>
/// 
/// </summary>
[DebuggerDisplay("{Value}")]
public readonly struct FileName :
    IEquatable<FileName>,
    IEqualityComparer<FileName>,
    IComparable<FileName>
#if NET7_0_OR_GREATER
    ,IEqualityOperators<FileName, FileName, bool>
#endif
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public FileName(string value)
    {
        ThrowHelper.ThrowIfNull(value, nameof(value));

        if (value.ContainsAny(Path.GetInvalidFileNameChars(), out var invalid))
        {
            ThrowHelper.ThrowArgumentException($"The file name has invalid characters `{invalid}`.");
        }

        Value = value;
    }

    /// <summary>
    /// The raw name value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets the file extension, if any.
    /// </summary>
    public string? Extension => Path.GetExtension(Value);

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
        return Value;
    }

    // <inheritdoc />
    public override int GetHashCode()
    {
        return Value.GetHashCode();
    }

    #endregion

    #region Methods
    public int CompareTo(FileName other)
    {
        return CompareTo(this, other, StringComparison.Ordinal);
    }

    public bool Equals(FileName other)
    {
        return Equals(this, other, StringComparison.Ordinal);
    }

    public bool Equals(FileName other, StringComparison comparison)
    {
        return Equals(this, other, comparison);
    }

    public bool Equals(FileName left, FileName right)
    {
        return Equals(left, right, StringComparison.Ordinal);
    }

    public int GetHashCode([DisallowNull] FileName obj)
    {
        return obj.GetHashCode();
    }

    public static bool Equals(FileName left, FileName right, StringComparison comparison)
    {
        return StringComparer.FromComparison(comparison).Equals(left.Value, right.Value);
    }

    public static int CompareTo(FileName left, FileName right, StringComparison comparison)
    {
        return StringComparer.FromComparison(comparison).Compare(left.Value, right.Value);
    }

    #endregion

    #region Operators


    public static implicit operator FileName(string value)
    {
        return new FileName(value);
    }

    public static implicit operator string(FileName name)
    {
        return name.Value;
    }

    public static implicit operator FileSystemPath(FileName name)
    {
        return new FileSystemPath("/" + name.Value);
    }

    public static bool operator ==(FileName left, FileName right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(FileName left, FileName right)
    {
        return left.Equals(right);
    }

    #endregion
}