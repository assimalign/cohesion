using System;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal class InMemoryFileSystemFile : IFileSystemFile
{
    public InMemoryFileSystemFile(FsFileNode node)
    {
        Node = node;
    }

    public FsFileNode Node { get; }
    public string Name => Path.GetDirectoryOrFileName()!;
    public Size Size => Node.Content.Length;
    public Path Path => throw new NotImplementedException();
    public DateTimeOffset UpdatedOn => Node.LastWriteTime;
    public DateTimeOffset CreatedOn => Node.CreationTime;
    public DateTimeOffset AccessedOn => Node.LastAccessTime;
    public IFileSystemDirectory Directory => new InMemoryFileSystemDirectory(Node.Parent!);

    public void Dispose()
    {
        
        throw new NotImplementedException();
    }

    public Stream Open()
    {
        throw new NotImplementedException();
    }

    public Stream Open(FileMode fileMode)
    {
        throw new NotImplementedException();
    }

    public Stream Open(FileMode fileMode, FileAccess fileAccess)
    {
        throw new NotImplementedException();
    }

    public Stream Open(FileMode fileMode, FileAccess fileAccess, FileShare fileShare)
    {
        throw new NotImplementedException();
    }

    public IFileSystemChangeToken Watch()
    {
        throw new NotImplementedException();
    }
}
