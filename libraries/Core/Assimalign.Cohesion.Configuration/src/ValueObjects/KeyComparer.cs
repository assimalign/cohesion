using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;

/// <summary>
/// 
/// </summary>
public class KeyComparer :
    IComparer<Key>,
    IEqualityComparer<Key>,
    IEqualityComparer<Path>,
    IAlternateEqualityComparer<ReadOnlySpan<char>, Key>
{
    private readonly KeyComparison _comparison;

    KeyComparer(KeyComparison comparison)
    {
        _comparison = comparison;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public int Compare(Key left, Key right)
    {
        return StringComparer.FromComparison((StringComparison)_comparison).Compare(left, right);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public int Compare(in Key left, in Key right)
    {
        return StringComparer.FromComparison((StringComparison)_comparison).Compare(left, right);
    }


    bool IEqualityComparer<Key>.Equals(Key left, Key right)
    {
        return StringComparer.FromComparison((StringComparison)_comparison).Equals(left, right);
    }

    bool IEqualityComparer<Path>.Equals(Path left, Path right)
    {
        return left.Equals(right, _comparison);
    }

    int IEqualityComparer<Path>.GetHashCode(Path obj)
    {
        return obj.GetHashCode();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public bool Equals(in Key left, in Key right)
    {
        return StringComparer.FromComparison((StringComparison)_comparison).Equals(left, right);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="obj"></param>
    /// <returns></returns>
    public int GetHashCode(Key obj)
    {
        return StringComparer.FromComparison((StringComparison)_comparison).GetHashCode(obj);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="comparison"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public static KeyComparer FromComparison(KeyComparison comparison)
    {
        return new KeyComparer(ThrowHelper.ThrowIfNotDefined(comparison));
    }

    public bool Equals(ReadOnlySpan<char> alternate, Key other)
    {
        return alternate.Equals(other, (StringComparison)_comparison);
    }

    public int GetHashCode(ReadOnlySpan<char> alternate)
    {
        return Create(alternate).GetHashCode();
    }

    public Key Create(ReadOnlySpan<char> alternate)
    {
        return new Key(alternate);
    }

    public static KeyComparer Ordinal { get; } = new KeyComparer(KeyComparison.Ordinal);
    public static KeyComparer OrdinalIgnoreCase { get; } = new KeyComparer(KeyComparison.OrdinalIgnoreCase);
    public static KeyComparer CurrentCulture { get; } = new KeyComparer(KeyComparison.CurrentCulture);
    public static KeyComparer CurrentCultureIgnoreCase { get; } = new KeyComparer(KeyComparison.CurrentCultureIgnoreCase);
    public static KeyComparer InvariantCulture { get; } = new KeyComparer(KeyComparison.InvariantCulture);
    public static KeyComparer InvariantCultureIgnoreCase { get; } = new KeyComparer(KeyComparison.InvariantCultureIgnoreCase);
}
