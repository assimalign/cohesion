using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace System.IO;

internal sealed class FileSystemPathComparer : 
    IComparer<FileName>,
    IEqualityComparer<FileName>,
    IComparer<DirectoryName>,
    IEqualityComparer<DirectoryName>,
    IComparer<FileSystemPath>,
    IEqualityComparer<FileSystemPath>
{
    public int Compare(FileName x, FileName y)
    {
        throw new NotImplementedException();
    }

    public int Compare(DirectoryName x, DirectoryName y)
    {
        throw new NotImplementedException();
    }

    public int Compare(FileSystemPath x, FileSystemPath y)
    {
        throw new NotImplementedException();
    }

    public bool Equals(FileName x, FileName y)
    {
        throw new NotImplementedException();
    }

    public bool Equals(DirectoryName x, DirectoryName y)
    {
        throw new NotImplementedException();
    }

    public bool Equals(FileSystemPath x, FileSystemPath y)
    {
        throw new NotImplementedException();
    }

    public int GetHashCode([DisallowNull] FileName obj)
    {
        throw new NotImplementedException();
    }

    public int GetHashCode([DisallowNull] DirectoryName obj)
    {
        throw new NotImplementedException();
    }

    public int GetHashCode([DisallowNull] FileSystemPath obj)
    {
        throw new NotImplementedException();
    }
}
