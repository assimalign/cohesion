using System;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;

namespace Assimalign.Cohesion.FileSystem.Internal;

/// <summary>
/// Represents a file on a physical file-system
/// </summary>
[DebuggerDisplay("{Path}")]
internal class PhysicalFileSystemFile : PhysicalFileSystemInfo, IFileSystemFile
{
    private readonly FileInfo _fileInfo;
    private readonly bool _isReadOnly;

    public PhysicalFileSystemFile(FileInfo fileInfo) : base(fileInfo)
    {
        _fileInfo = fileInfo;
    }

    public FileName Name => _fileInfo.Name;
    public Size Size => _fileInfo.Length;
    public IFileSystemDirectory Directory => new PhysicalFileSystemDirectory(_fileInfo.Directory!);
    public IFileSystemChangeToken Watch()
    {
        return new PhysicalFileSystemChangeToken(this);
    }
    public Stream Open()
    {
        return Open(FileMode.Open);
    }
    public Stream Open(FileMode fileMode)
    {
        return Open(fileMode, FileSystem.IsReadOnly ? FileAccess.Read : FileAccess.ReadWrite);
    }
    public Stream Open(FileMode fileMode, FileAccess fileAccess)
    {
        return Open(fileMode, fileAccess, FileShare.None);
    }
    public Stream Open(FileMode fileMode, FileAccess fileAccess, FileShare fileShare)
    {
        bool isAllowed = (fileMode != FileMode.Open || fileAccess != FileAccess.Read) && FileSystem.IsReadOnly;

        if (isAllowed)
        {

        }

        return File.Open(Path, fileMode, fileAccess, fileShare);
    }

    public void Dispose()
    {

    }
}
