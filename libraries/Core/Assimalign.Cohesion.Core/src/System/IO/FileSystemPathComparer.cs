using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace System.IO;

using Assimalign.Cohesion.Internal;

/// <summary>
/// 
/// </summary>
public sealed class FileSystemPathComparer : IEqualityComparer<FileSystemPath>, IComparer<FileSystemPath>
{
    private readonly CultureInfo _cultureInfo;

    private FileSystemPathComparer(CultureInfo cultureInfo)
    {
        _cultureInfo = cultureInfo;
    }

    public int Compare(FileSystemPath left, FileSystemPath right)
    {
        return left.CompareTo(right, _cultureInfo);
    }

    public bool Equals(FileSystemPath left, FileSystemPath right)
    {
        return left.Equals(right, _cultureInfo);
    }

    public int GetHashCode([DisallowNull] FileSystemPath path)
    {
        return path.GetHashCode(_cultureInfo);
    }

    public static FileSystemPathComparer Create(CultureInfo cultureInfo)
    {
        ThrowHelper.ThrowIfNull(cultureInfo, nameof(cultureInfo));

        return new FileSystemPathComparer(cultureInfo);
    }

    public static FileSystemPathComparer CurrentCulture { get; } = new FileSystemPathComparer(CultureInfo.InvariantCulture);
    public static FileSystemPathComparer InvariantCulture { get; } = new FileSystemPathComparer(CultureInfo.InvariantCulture);
}
