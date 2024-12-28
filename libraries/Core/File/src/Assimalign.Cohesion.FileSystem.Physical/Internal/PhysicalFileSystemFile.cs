using System;
using System.IO;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem.Internal;

/// <summary>
/// Represents a file on a physical file-system
/// </summary>
internal class PhysicalFileSystemFile : IFileSystemFile
{
    private readonly FileInfo fileInfo;

    private FileStream? stream;
    private bool isOpen;

    public PhysicalFileSystemFile(FileInfo fileInfo)
    {
        this.fileInfo = fileInfo;
    }

    public PhysicalFileSystemFile(Path path)
    {
        this.fileInfo = new FileInfo(path);
    }

    public string Name => fileInfo.Name;
    public Path Path => fileInfo.FullName;
    public Size Size => fileInfo.Length;
    public DateTimeOffset UpdatedOn => fileInfo.LastWriteTimeUtc;
    public DateTimeOffset CreatedOn => fileInfo.CreationTimeUtc;
    public DateTimeOffset AccessedOn => fileInfo.LastAccessTimeUtc;
    public IFileSystemDirectory Directory => new PhysicalFileSystemDirectory(fileInfo.Directory!);

    //private FileStream TryOpen()
    //{
    //    if (isOpen) return stream!;
    //    // We are setting buffer size to 1 to prevent FileStream from allocating it's internal buffer
    //    // 0 causes constructor to throw
    //    int bufferSize = 1;
    //    stream ??= new FileStream(
    //        Path,
    //        FileMode.Open,
    //        FileAccess.ReadWrite,
    //        FileShare.ReadWrite,
    //        bufferSize,
    //        FileOptions.Asynchronous | FileOptions.SequentialScan);
    //    isOpen = true;
    //    return stream;
    //}

    public Stream Open()
    {
        throw new NotImplementedException();
    }

    public Stream Open(FileMode fileMode)
    {
        if (isOpen)
        {
            ThrowHelper.ThrowInvalidOperationException("The file is already open.");
        }

        stream = File.Open(Path, fileMode);
        isOpen = true;

        return stream;
    }

    public Stream Open(FileMode fileMode, FileAccess fileAccess)
    {
        if (isOpen)
        {
            ThrowHelper.ThrowInvalidOperationException("The file is already open.");
        }

        stream = File.Open(Path, fileMode, fileAccess);
        isOpen = true;

        return stream;
    }

    public Stream Open(FileMode fileMode, FileAccess fileAccess, FileShare fileShare)
    {
        if (isOpen)
        {
            ThrowHelper.ThrowInvalidOperationException("The file is already open.");
        }

        stream = File.Open(Path, fileMode, fileAccess, fileShare);
        isOpen = true;

        return stream;
    }


    public void Dispose()
    {
        if (isOpen)
        {
            stream!.Close();
        }
    }

    public IFileSystemChangeToken Watch()
    {
        throw new NotImplementedException();
    }

    
}
