using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace System.IO;

using Assimalign.Cohesion.Internal;

/// <summary>
/// 
/// </summary>
public sealed class FileSystemPathComparer :
    IComparer<FileSystemPath>
    , IEqualityComparer<FileSystemPath>
    , IAlternateEqualityComparer<ReadOnlySpan<char>, FileSystemPath>
{
    private readonly StringComparison _comparison;

    private FileSystemPathComparer(StringComparison comparison)
    {
        _comparison = comparison;
    }

    public int Compare(FileSystemPath left, FileSystemPath right)
    {
        return left.CompareTo(right, _comparison);
    }

    public bool Equals(FileSystemPath left, FileSystemPath right)
    {
        return left.Equals(right, _comparison);
    }

    public int GetHashCode([DisallowNull] FileSystemPath path)
    {
        return path.GetHashCode(_comparison);
    }

    bool IAlternateEqualityComparer<ReadOnlySpan<char>, FileSystemPath>.Equals(ReadOnlySpan<char> alternate, FileSystemPath other)
    {
        return alternate.Equals(other, _comparison);
    }

    int IAlternateEqualityComparer<ReadOnlySpan<char>, FileSystemPath>.GetHashCode(ReadOnlySpan<char> alternate)
    {
        return FileSystemPath.Parse(alternate).GetHashCode(_comparison);
    }

    FileSystemPath IAlternateEqualityComparer<ReadOnlySpan<char>, FileSystemPath>.Create(ReadOnlySpan<char> alternate)
    {
        return alternate;
    }

    public static FileSystemPathComparer Create(StringComparison comparison)
    {
        return new FileSystemPathComparer(comparison);
    }

    public static FileSystemPathComparer CurrentCulture { get; } = new FileSystemPathComparer(StringComparison.InvariantCulture);
    public static FileSystemPathComparer InvariantCulture { get; } = new FileSystemPathComparer(StringComparison.InvariantCulture);
}
