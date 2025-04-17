using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;

/// <summary>
/// 
/// </summary>
public class KeyComparer : IComparer<Key>, IEqualityComparer<Key>
{
    private readonly StringComparer _comparer;

    KeyComparer(KeyComparison comparison)
    {
        _comparer = StringComparer.FromComparison((StringComparison)comparison);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public int Compare(Key left, Key right)
    {
        return _comparer.Compare(left, right);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public int Compare(in Key left, in Key right)
    {
        return _comparer.Compare(left, right);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    bool IEqualityComparer<Key>.Equals(Key left, Key right)
    {
        return _comparer.Equals(left, right);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public bool Equals(in Key left, in Key right)
    {
        return _comparer.Equals(left, right);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="obj"></param>
    /// <returns></returns>
    public int GetHashCode(Key obj)
    {
        return _comparer.GetHashCode(obj);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="comparison"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public static KeyComparer FromComparison(KeyComparison comparison)
    {
        if (Enum.IsDefined(comparison))
        {
            throw new ArgumentException("Invalid comparison.");
        }
        return new KeyComparer(comparison);
    }

    public static KeyComparer Ordinal { get; } = new KeyComparer(KeyComparison.Ordinal);
    public static KeyComparer OrdinalIgnoreCase { get; } = new KeyComparer(KeyComparison.OrdinalIgnoreCase);
    public static KeyComparer CurrentCulture { get; } = new KeyComparer(KeyComparison.CurrentCulture);
    public static KeyComparer CurrentCultureIgnoreCase { get; } = new KeyComparer(KeyComparison.CurrentCultureIgnoreCase);
    public static KeyComparer InvariantCulture { get; } = new KeyComparer(KeyComparison.InvariantCulture);
    public static KeyComparer InvariantCultureIgnoreCase { get; } = new KeyComparer(KeyComparison.InvariantCultureIgnoreCase);
}
