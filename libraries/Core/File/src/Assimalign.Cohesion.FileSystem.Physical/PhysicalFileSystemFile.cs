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

    private bool isOpen;
    private FileStream? stream;

    public PhysicalFileSystemFile(FileInfo fileInfo)
    {
        this.fileInfo = fileInfo;
    }

    public PhysicalFileSystemFile(string path)
    {
        this.fileInfo = new FileInfo(path);
    }

    

    public FileName Name => fileInfo.Name;
    public FilePath Path => fileInfo.FullName;
    public FileSize Length
    {
        get
        {
            return TryOpen()!.Length;
        }
    }
    public DateTimeOffset UpdatedDateTime => fileInfo.LastWriteTimeUtc;
    public DateTimeOffset CreatedDateTime => fileInfo.CreationTimeUtc;
    public bool IsDirectory => false;
    public bool IsFile => true;
    public IFileSystemDirectory Directory => new PhysicalFileSystemDirectory(fileInfo.Directory!);
    public Stream Stream
    {
        get
        {
            return TryOpen();
        }
    }

    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    public int Read(Span<byte> buffer, long offset)
    {
        var stream = TryOpen();

        return RandomAccess.Read(stream.SafeFileHandle, buffer, offset);
    }

    public int Read(Span<byte> buffer, long position, long offset)
    {
        var stream = TryOpen();

        stream.Position = position;

        return RandomAccess.Read(stream.SafeFileHandle, buffer, offset);
    }

    public void Write(Span<byte> buffer, long offset)
    {
        throw new NotImplementedException();
    }

    public void Write(Span<byte> buffer, long position, long offset)
    {
        throw new NotImplementedException();
    }

    public ValueTask<int> ReadAsync(Span<byte> buffer, long position, long offset)
    {
        throw new NotImplementedException();
    }

    public ValueTask WriteAsync(Span<byte> buffer, long position, long offset)
    {
        throw new NotImplementedException();
    }


    private FileStream TryOpen()
    {
        if (isOpen) return stream!;
        // We are setting buffer size to 1 to prevent FileStream from allocating it's internal buffer
        // 0 causes constructor to throw
        int bufferSize = 1;
        stream ??= new FileStream(
            Path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        isOpen = true;
        return stream;
    }
}
