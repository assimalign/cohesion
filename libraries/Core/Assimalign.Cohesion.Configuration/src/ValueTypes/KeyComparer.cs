using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// 
/// </summary>
public class KeyComparer : IComparer<Key>, IEqualityComparer<Key>
{
    private StringComparer comparer;

    internal KeyComparer(KeyComparison comparison)
    {
        this.comparer = comparison switch
        {
            KeyComparison.Ordinal => StringComparer.Ordinal,
            KeyComparison.OrdinalIgnoreCase => StringComparer.OrdinalIgnoreCase
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


    public static KeyComparer Ordinal { get; } = new KeyComparer(KeyComparison.Ordinal);
    public static KeyComparer OrdinalIgnoreCase { get; } = new KeyComparer(KeyComparison.OrdinalIgnoreCase);
}
