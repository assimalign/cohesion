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
    private readonly CultureInfo _cultureInfo;
    private readonly bool _ignoreCase;
    private readonly StringComparer _comparer;

    private FileSystemPathComparer(CultureInfo cultureInfo, bool ignoreCase = false)
    {
        _cultureInfo = cultureInfo;
        _ignoreCase = ignoreCase;
        _comparer = StringComparer.Create(cultureInfo, ignoreCase);
    }

    public int Compare(FileSystemPath left, FileSystemPath right)
    {
        return left.CompareTo(right, _cultureInfo, _ignoreCase);
    }
    public bool Equals(FileSystemPath left, FileSystemPath right)
    {
        return left.Equals(right, _cultureInfo, _ignoreCase);
    }
    public int GetHashCode([DisallowNull] FileSystemPath path)
    {
        return path.GetHashCode(_cultureInfo, _ignoreCase);
    }

    bool IAlternateEqualityComparer<ReadOnlySpan<char>, FileSystemPath>.Equals(ReadOnlySpan<char> alternate, FileSystemPath other)
    {
        return ((IAlternateEqualityComparer<ReadOnlySpan<char>, string>)_comparer).Equals(alternate, other);
    }

    int IAlternateEqualityComparer<ReadOnlySpan<char>, FileSystemPath>.GetHashCode(ReadOnlySpan<char> alternate)
    {
        int code =  ((IAlternateEqualityComparer<ReadOnlySpan<char>, string>)_comparer).GetHashCode(alternate);
        return (int)((uint)code | ((uint)code << 16));
    }

    FileSystemPath IAlternateEqualityComparer<ReadOnlySpan<char>, FileSystemPath>.Create(ReadOnlySpan<char> alternate)
    {
        return alternate;
    }

    public static FileSystemPathComparer Create(CultureInfo cultureInfo, bool ignoreCase = false)
    {
        return new FileSystemPathComparer(cultureInfo, ignoreCase);
    }
}
