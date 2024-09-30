using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion;

using Internal;

/// <summary>
/// A case-insensitive representation of a file path.
/// </summary>
/// <remarks>
/// Comparing file paths as strings can be dangerous as different OS's have 
/// case-sensitive file system such as linux. The following approach can be 
/// done with a class, or struct as well.
/// </remarks>
[DebuggerDisplay("{Value}")]
public readonly struct Path : IEquatable<Path>, IEqualityComparer<Path>, IComparable<Path>
{
    public Path(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            ThrowHelper.ThrowArgumentException($"{nameof(path)} cannot be null or empty");
        }
        else if (System.IO.Path.GetInvalidPathChars().Intersect(path).Any())
        {
            ThrowHelper.ThrowArgumentException($"{nameof(path)} contains illegal characters");
        }
        else
        {
            //Value = Format(System.IO.Path.GetFullPath(path.Trim()));
            Value = Format(path.Trim());
        }
    }

    private string Format(string path)
    {
        return string.Create(path.Length, path, (span, value) =>
        {
            value.CopyTo(span);
            // Let's convert all forward slashes to backward slashes
            for (int i = 0; i < span.Length;i++)
            {
                if (span[i] == '/')
                {
                    span[i] = '\\';
                }
            }
        });
    }

    /// <summary>
    /// The raw path.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// 
    /// </summary>
    public bool HasRoot
    {
        get
        {
            return System.IO.Path.IsPathRooted(Value);
        }
    }

    public string? GetDirectoryOrFileName()
    {
        var index = Value.LastIndexOf('\\');

        if (index == -1)
        {
            return null;
        }

        var name = Value.Substring(index + 1, Value.Length - (index + 1));

        if (name == string.Empty || name.Contains("*"))
        {
            return null;
        }

        return name;
    }

    public static Path Empty => "\\";

    /// <summary>
    /// 
    /// </summary>
    /// <param name="paths"></param>
    /// <returns></returns>
    public Path Concat(params string[] paths)
    {
        return System.IO.Path.Combine(paths.Prepend(Value).ToArray());
    }

    public static Path Combine(params string[] paths)
    {
        return System.IO.Path.Combine(paths);
    }


    #region Overloads
    public override bool Equals(object? obj)
    {
        return obj is Path path ? Equals(path) : false;
    }
    public override string ToString()
    {
        return Value;
    }
    public override int GetHashCode()
    {
        return HashCode.Combine(typeof(Path), Value);
    }
    #endregion

    #region Interfaces
    public bool Equals(Path other)
    {
        return Path.Equals(other.Value, StringComparison.InvariantCultureIgnoreCase);
    }
    public bool Equals(Path right, Path left)
    {
        return right!.Equals(left);
    }
    public int GetHashCode([DisallowNull] Path obj)
    {
        return obj.GetHashCode();
    }
    public int CompareTo(Path other)
    {
        return string.Compare(Value, other.Value, StringComparison.InvariantCultureIgnoreCase);
    }

    #endregion

    #region Operators
    public static implicit operator Path(string name) => new(name);

    public static implicit operator string(Path path) => path.Value;
    public static bool operator ==(Path left, Path right) => left.Equals(right);
    public static bool operator !=(Path left, Path right) => !left.Equals(right);
    public static bool operator >(Path left, Path right) => left.CompareTo(right) > 0;
    public static bool operator <(Path left, Path right) => left.CompareTo(right) < 0;
    public static bool operator >=(Path left, Path right) => left.CompareTo(right) >= 0;
    public static bool operator <=(Path left, Path right) => left.CompareTo(right) <= 0;
    #endregion
}