using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;

/// <summary>
/// 
/// </summary>
public class KeyComparer : IComparer<Key>, IEqualityComparer<Key>//, IComparer<KeyPath>
{
    private readonly StringComparer comparer;

    internal KeyComparer(KeyComparison comparison)
    {
        this.comparer = comparison switch
        {
            KeyComparison.Ordinal => StringComparer.Ordinal,
            KeyComparison.OrdinalIgnoreCase => StringComparer.OrdinalIgnoreCase,
            _ => throw ThrowHelper.GetArgumentException("")
        };
    }


    public int Compare(Key left, Key right)
    {
        return comparer.Compare(left, right);
    }

    public bool Equals(Key left, Key right)
    {
        return comparer.Equals(left, right);
    }

    public int GetHashCode([DisallowNull] Key obj)
    {
        return obj.GetHashCode();
    }

    //public int Compare(KeyPath x, KeyPath y)
    //{
    //    string[] xParts = x?.Split(KeyPath.Delimiters, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
    //    string[] yParts = y?.Split(_keyDelimiterArray, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();

    //    // Compare each part until we get two parts that are not equal
    //    for (int i = 0; i < Math.Min(xParts.Length, yParts.Length); i++)
    //    {
    //        x = xParts[i];
    //        y = yParts[i];

    //        int value1 = 0;
    //        int value2 = 0;

    //        bool xIsInt = x != null && int.TryParse(x, out value1);
    //        bool yIsInt = y != null && int.TryParse(y, out value2);

    //        int result;

    //        if (!xIsInt && !yIsInt)
    //        {
    //            // Both are strings
    //            result = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
    //        }
    //        else if (xIsInt && yIsInt)
    //        {
    //            // Both are int
    //            result = value1 - value2;
    //        }
    //        else
    //        {
    //            // Only one of them is int
    //            result = xIsInt ? -1 : 1;
    //        }

    //        if (result != 0)
    //        {
    //            // One of them is different
    //            return result;
    //        }
    //    }

    //    // If we get here, the common parts are equal.
    //    // If they are of the same length, then they are totally identical
    //    return xParts.Length - yParts.Length;
    //}

    public static KeyComparer Ordinal { get; } = new KeyComparer(KeyComparison.Ordinal);
    public static KeyComparer OrdinalIgnoreCase { get; } = new KeyComparer(KeyComparison.OrdinalIgnoreCase);
}
