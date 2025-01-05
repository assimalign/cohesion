
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections;


namespace Assimalign.Cohesion.FileSystem.Internal;

/// <summary>
/// Avoids using disk for uses like Pattern Matching.
/// </summary>
internal class InMemoryFileSystemDirectory : IFileSystemDirectory
{
    public InMemoryFileSystemDirectory(FsDirectoryNode node)
    {
        Node = node;
    }

    public FsDirectoryNode Node { get; }
    public IFileSystemDirectory? Parent => throw new NotImplementedException();
    public string Name => throw new NotImplementedException();
    public Path Path => throw new NotImplementedException();
    public DateTimeOffset UpdatedOn => throw new NotImplementedException();
    public DateTimeOffset CreatedOn => throw new NotImplementedException();
    public DateTimeOffset AccessedOn => throw new NotImplementedException();

    public bool Exist(Path path)
    {
        throw new NotImplementedException();
    }

    public IEnumerable<IFileSystemDirectory> GetDirectories()
    {
        throw new NotImplementedException();
    }

    public IFileSystemDirectory GetDirectory(Path path)
    {
        throw new NotImplementedException();
    }

    public IEnumerator<IFileSystemInfo> GetEnumerator()
    {
        throw new NotImplementedException();
    }

    public IFileSystemFile GetFile(Path path)
    {
        throw new NotImplementedException();
    }

    public IEnumerable<IFileSystemInfo> GetFiles()
    {
        throw new NotImplementedException();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        throw new NotImplementedException();
    }
}
