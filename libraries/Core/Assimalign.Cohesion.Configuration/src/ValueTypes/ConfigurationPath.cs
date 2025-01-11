using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;
using System.Linq;


/*
	Accepted Path Separators:
	
	- Slash Format: 			"/key1/"
	- Backward Slash Format:	"\\key1\\key2[index]\\key3"
	- Namespace Format:			"key1.key2[index].key3
	- Colon Format:				"key1:key2[index]:key3"
	- Mixed Format:				"/key1.key2\\key3[index]:key4"
	

	Path with Regex:
	
	- key1:`[A-c]|` use the backtick to escape 


    Path with Wildcard
*/
[DebuggerDisplay("{ToString()}")]
public readonly struct ConfigurationPath : IEquatable<ConfigurationPath>, IEqualityComparer<ConfigurationPath>, IComparable<ConfigurationPath>
{
    private static char[] separators => ['\\', '/', ':', '.'];
    
    public ConfigurationPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            ThrowHelper.ThrowArgumentNullException(nameof(path));
        }

        var value = path.Trim(separators);
        var indexOfSep = 0;

        var list = new V

        for (int i = indexOfSep; i < value.Length; i++)
        {
            var c = value[i];

            // Regex expression escape 
            if (c == '`')
            {
                i++;
                var start = i;

                for (; value[i] != '`'; i++)
                {
                    if ((i + 1) == value.Length)
                    {
                        ThrowHelper.ThrowArgumentException($"The regex expression at index {start} is missing closing backtick.");
                    }
                }
            }
            if (Separators.Contains(c))
            {
                var key = value.Substring(indexOfSep, i - indexOfSep);

                

                indexOfSep = i + 1;
            }
        }
    }


    /// <summary>
    /// Returns the default separator.
    /// </summary>
    public const char DefaultSeparator = ':';
    /// <summary>
    /// Allowed separators.
    /// </summary>
    public static ReadOnlySpan<char> Separators => separators;
    /// <summary>
    /// The configuration keys that make up the path.
    /// </summary>
    public ConfigurationKey[] Keys { get; }
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
        if (obj is ConfigurationPath path)
        {
            return Equals(path);
        }
        return false;
    }
    public override int GetHashCode()
    {
        return base.GetHashCode();
    }
    #endregion

    #region Interfaces
    public bool Equals(ConfigurationPath other)
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
    public bool Equals(ConfigurationPath left, ConfigurationPath right)
    {
        return left.Equals(right);
    }
    public int GetHashCode(ConfigurationPath path)
    {
        return path.GetHashCode();
    }
    public int CompareTo(ConfigurationPath other)
    {
        throw new NotImplementedException();
    }
    #endregion

    #region Operators
    public static implicit operator string(ConfigurationPath path)
    {
        return path.ToString();
    }
    public static implicit operator ConfigurationPath(string path)
    {
        return new ConfigurationPath(path);
    }
    #endregion

    #region Helpers
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

        int lastDelimiterIndex = path.LastIndexOfAny([.. Separators]);
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

        int lastDelimiterIndex = path.LastIndexOfAny([.. Separators]);
        return lastDelimiterIndex == -1 ? null : path.Substring(0, lastDelimiterIndex);
    }

    #endregion
}


