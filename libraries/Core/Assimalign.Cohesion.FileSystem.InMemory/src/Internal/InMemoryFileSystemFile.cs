using System;
using System.IO;
using System.Threading;
using System.Diagnostics;

namespace Assimalign.Cohesion.FileSystem.Internal;

using Assimalign.Cohesion.Internal;

[DebuggerDisplay("f - {Path}")]
internal class InMemoryFileSystemFile : InMemoryFileSystemInfo, IFileSystemFile
{
    private bool _isDiposed;

    public InMemoryFileSystemFile(FileName name, InMemoryFileSystemDirectory directory, InMemoryFileSystem fileSystem) : base(fileSystem)
    {
        Name = name;
        Directory = directory;
        Content = new InMemoryFileContent(this);
    }

    public Size Size => Content.Length;
    public FileName Name { get; }
    public InMemoryFileSystemDirectory Directory { get; }
    public InMemoryFileContent Content { get; private set; }
    IFileSystemDirectory IFileSystemFile.Directory => Directory;
    public override void Dispose()
    {
        Directory.DeleteFile(Name);
    }

    public Stream Open()
    {
        return Open(FileMode.Open);
    }

    public Stream Open(FileMode fileMode)
    {
        return Open(fileMode, FileAccess.ReadWrite);
    }

    public Stream Open(FileMode fileMode, FileAccess fileAccess)
    {
        return Open(fileMode, fileAccess, FileShare.None);
    }

    public Stream Open(FileMode fileMode, FileAccess fileAccess, FileShare fileShare)
    {
        if (fileMode == FileMode.Append && (fileAccess & FileAccess.Read) != 0)
        {
            throw new ArgumentException("Combining FileMode: Append with FileAccess: Read is invalid.", nameof(fileAccess));
        }

        var isReading = (fileAccess & FileAccess.Read) != 0;
        var isWriting = (fileAccess & FileAccess.Write) != 0;
        var isExclusive = fileShare == FileShare.None;

        if (isExclusive)
        {
            Lock(LockPolicy.Exclusive);
        }
        else
        {
            Lock(LockPolicy.Read | LockPolicy.Write);
        }

        var stream = new InMemoryFileStream(
            this, 
            isReading, 
            isWriting, 
            isExclusive);

        if (fileMode == FileMode.Append)
        {
            stream.Position = stream.Length;
        }
        else if (fileMode == FileMode.Truncate)
        {
            stream.SetLength(0);
        }

        return stream;

        // Append:      Opens the file if it exists and seeks to the end of the file, or creates a new file. 
        //              This requires FileIOPermissionAccess.Append permission. FileMode.Append can be used only in 
        //              conjunction with FileAccess.Write. Trying to seek to a position before the end of the file 
        //              throws an IOException exception, and any attempt to read fails and throws a 
        //              NotSupportedException exception.
        //
        //
        // CreateNew:   Specifies that the operating system should create a new file.This requires 
        //              FileIOPermissionAccess.Write permission. If the file already exists, an IOException 
        //              exception is thrown.
        //
        // Open:        Specifies that the operating system should open an existing file. The ability to open 
        //              the file is dependent on the value specified by the FileAccess enumeration. 
        //              A System.IO.FileNotFoundException exception is thrown if the file does not exist.
        //
        // Open/Create: Specifies that the operating system should open a file if it exists; 
        //              otherwise, a new file should be created. If the file is opened with 
        //              FileAccess.Read, FileIOPermissionAccess.Read permission is required. 
        //              If the file access is FileAccess.Write, FileIOPermissionAccess.Write permission 
        //              is required. If the file is opened with FileAccess.ReadWrite, both 
        //              FileIOPermissionAccess.Read and FileIOPermissionAccess.Write permissions 
        //              are required. 
        //
        // Truncate:    Specifies that the operating system should open an existing file. 
        //              When the file is opened, it should be truncated so that its size is zero bytes. 
        //              This requires FileIOPermissionAccess.Write permission. Attempts to read from a file 
        //              opened with FileMode.Truncate cause an ArgumentException exception.
        //
        // Create:      Specifies that the operating system should create a new file.If the file already exists, 
        //              it will be overwritten.This requires FileIOPermissionAccess.Write permission. 
        //              FileMode.Create is equivalent to requesting that if the file does not exist, use CreateNew; 
        //              otherwise, use Truncate. If the file already exists but is a hidden file, 
        //              an UnauthorizedAccessException exception is thrown.
    }

    public void Close(bool isShared)
    {
        Unlock();
    }
    public IFileSystemChangeToken Watch()
    {
        return default!;// GetToken(this);
    }
}
