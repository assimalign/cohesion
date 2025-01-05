using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Configuration;

using Internal;

[DebuggerDisplay("{ToString()}")]
public readonly struct ConfigPath : IEquatable<ConfigPath>, IEqualityComparer<ConfigPath>, IComparable<ConfigPath>
{
    private static ReadOnlySpan<char> separators => ['\\', ':', '.'];

    /*
        {key1}:{key2}
        {key1}/{key2}
        {key1}.{key2}
     */
    public ConfigPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            ThrowHelper.ThrowArgumentNullException(nameof(path));
        }

        var entries = path.Split([.. separators], StringSplitOptions.TrimEntries) ?? [];

        if (entries.Length > MaxDepth)
        {
            ThrowHelper.ThrowArgumentException("");
        }

        var keys = new ConfigKey[entries.Length];

        for (int i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];

            keys[i] = new ConfigKey();
        }

        Keys = keys;
    }

    /// <summary>
    /// The max allowed keys in the path.
    /// </summary>
    public const int MaxDepth = 25;
    /// <summary>
    /// Returns the default separator.
    /// </summary>
    public const char DefaultSeparator = ':';
    /// <summary>
    /// The configuration keys that make up the path.
    /// </summary>
    public ConfigKey[] Keys { get; }
    /// <summary>
    /// 
    /// </summary>
    public int Length
    {
        get
        {
            return Keys.Length;
        }
    }
    /// <summary>
    /// Returns an array of valid separators for a path.
    /// </summary>
    /// <returns></returns>
    public static char[] GetSeparators()
    {
        return separators.ToArray();
    }


    #region Overloads
    /// <summary>
    /// Returns a formatted full path.
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return string.Join(DefaultSeparator, Keys);
    }
    public override bool Equals(object? obj)
    {
        return base.Equals(obj);
    }
    public override int GetHashCode()
    {
        return base.GetHashCode();
    }
    #endregion

    #region Interfaces
    public bool Equals(ConfigPath other)
    {
        if (other.Length == Length)
        {
            return false;
        }
        for (int i = 0; i < Length; i++)
        {
            var left = Keys[i];
            var right = other.Keys[i];

            if (left != right)
            {
                return false;
            }
        }
        return true;
    }

    public bool Equals(ConfigPath left, ConfigPath right)
    {
        return left.Equals(right);
    }

    public int GetHashCode(ConfigPath obj)
    {
        throw new NotImplementedException();
    }

    public int CompareTo(ConfigPath other)
    {
        throw new NotImplementedException();
    }


    #endregion

    #region Operators
    public static implicit operator string(ConfigPath path)
    {
        return path.ToString();
    }
    public static implicit operator ConfigPath(string path)
    {
        return new ConfigPath(path);
    }
    #endregion


    #region Helpers
    /// <summary>
    /// Returns a list of valid separators.
    /// </summary>
    /// <returns></returns>
    public static char[] GetValidSeparators()
    {
        return separators.ToArray();
    }
    /// <summary>
    /// Combines path segments into one path.
    /// </summary>
    /// <param name="pathSegments">The path segments to combine.</param>
    /// <returns>The combined path.</returns>
    public static string Combine(params string[] pathSegments)
    {
        if (pathSegments == null)
        {
            throw new ArgumentNullException(nameof(pathSegments));
        }
        return string.Join(DefaultSeparator, pathSegments);
    }

    /// <summary>
    /// Combines path segments into one path.
    /// </summary>
    /// <param name="pathSegments">The path segments to combine.</param>
    /// <returns>The combined path.</returns>
    public static string Combine(IEnumerable<string> pathSegments)
    {
        if (pathSegments == null)
        {
            throw new ArgumentNullException(nameof(pathSegments));
        }
        return string.Join(DefaultSeparator, pathSegments);
    }

    /// <summary>
    /// Extracts the last path segment from the path.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The last path segment of the path.</returns>
    public static string GetSectionKey(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        int lastDelimiterIndex = path.LastIndexOfAny([..separators]);
        return lastDelimiterIndex == -1 ? path : path.Substring(lastDelimiterIndex + 1);
    }

    /// <summary>
    /// Extracts the path corresponding to the parent node for a given path.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The original path minus the last individual segment found in it. Null if the original path corresponds to a top level node.</returns>
    public static string GetParentPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        int lastDelimiterIndex = path.LastIndexOfAny([.. separators]);
        return lastDelimiterIndex == -1 ? null : path.Substring(0, lastDelimiterIndex);
    }

    #endregion
}
