using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.RegularExpressions;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;


[DebuggerDisplay("{ToString()}")]
public readonly struct ConfigurationKey : IEquatable<ConfigurationKey>
{
    /*
        {key1}:{key2}
        {key1}/{key2}[i]/
     */

    private readonly char[] chars;

    private readonly string? pattern;

    public ConfigurationKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            ThrowHelper.ThrowArgumentNullException(nameof(key));
        }

        chars = new char[key.Length];

        for (int i = 0; i < key.Length; i++)
        {
            chars[i] = key[i];

            if (key[i] == '[')
                {
                    if (key.Length <= (i + 1))
                    {
                        ThrowHelper.ThrowArgumentException("");
                    }

                    i++;
                    var start = i;
                    var index = new char[(key.Length - 1) - i]; // 2 accounts for the brackets

                    while (key[i] != ']')
                    {
                        if (key.Length <= (i + 1))
                        {
                            ThrowHelper.ThrowArgumentException("");
                        }

                        index[i - start] = key[i];
                        chars[i] = key[i];
                        i++;
                    }
                    if ((i + 1) != key.Length)
                    {
                        // This means a key was placed n the middle of a string. exp: my[2]key
                        ThrowHelper.ThrowArgumentException("");
                    }

                    Index = int.Parse(index);
                }
        }
    }

    /// <summary>
    /// 
    /// </summary>
    public int? Index { get; }
    /// <summary>
    /// 
    /// </summary>
    public bool HasIndex => Index is not null;

    public bool HasWildcard { get; }

    public bool HasRegex => pattern is not null;

    #region Overloads
    public override bool Equals([NotNullWhen(true)] object? obj)
    {
        return base.Equals(obj);
    }
    public override int GetHashCode()
    {
        return base.GetHashCode();
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return new string(chars);
    }

    public bool Equals(ConfigurationKey other)
    {

        throw new NotImplementedException();
    }


    #endregion

    public static implicit operator ConfigurationKey(string value)
    {
        return new ConfigurationKey(value);
    }
    public static bool operator ==(ConfigurationKey left, ConfigurationKey right)
    {
        return true;
    }
    public static bool operator !=(ConfigurationKey left, ConfigurationKey right)
    {
        return true;
    }


}