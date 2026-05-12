using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;



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
    private readonly StringComparer _stringComparer;
    private readonly StringComparison _stringComparison;

    KeyComparer(KeyComparison comparison)
    {
        _comparison = comparison;
        _stringComparison = (StringComparison)comparison;
        _stringComparer = StringComparer.FromComparison(_stringComparison);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public int Compare(Key left, Key right)
    {
        return _stringComparer.Compare(left.ToString(), right.ToString());
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public int Compare(in Key left, in Key right)
    {
        return _stringComparer.Compare(left.ToString(), right.ToString());
    }


    bool IEqualityComparer<Key>.Equals(Key left, Key right)
    {
        return Equals(in left, in right);
    }

    bool IEqualityComparer<Path>.Equals(Path left, Path right)
    {
        return left.Equals(right, _comparison);
    }

    int IEqualityComparer<Path>.GetHashCode(Path obj)
    {
        return obj.GetHashCode(_comparison);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public bool Equals(in Key left, in Key right)
    {
        return left.AsSpan().Equals(right.AsSpan(), _stringComparison);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="obj"></param>
    /// <returns></returns>
    public int GetHashCode(Key obj)
    {
        return obj.GetHashCode(_comparison);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="comparison"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public static KeyComparer FromComparison(KeyComparison comparison)
    {
        return ArgumentException.ThrowIfEnumNotDefined(comparison) switch
        {
            KeyComparison.Ordinal => Ordinal,
            KeyComparison.OrdinalIgnoreCase => OrdinalIgnoreCase,
            KeyComparison.CurrentCulture => CurrentCulture,
            KeyComparison.CurrentCultureIgnoreCase => CurrentCultureIgnoreCase,
            KeyComparison.InvariantCulture => InvariantCulture,
            KeyComparison.InvariantCultureIgnoreCase => InvariantCultureIgnoreCase,
            _ => throw new ArgumentOutOfRangeException(nameof(comparison))
        };
    }

    public bool Equals(ReadOnlySpan<char> alternate, Key other)
    {
        return alternate.Equals(other.AsSpan(), _stringComparison);
    }

    public int GetHashCode(ReadOnlySpan<char> alternate)
    {
        return string.GetHashCode(alternate, _stringComparison);
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
