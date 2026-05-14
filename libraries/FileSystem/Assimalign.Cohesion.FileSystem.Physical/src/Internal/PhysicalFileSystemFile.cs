using System;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;

namespace Assimalign.Cohesion.FileSystem.Internal;

using Assimalign.Cohesion.Internal;

[DebuggerDisplay("[F] - {Path}")]
internal class PhysicalFileSystemFile : PhysicalFileSystemInfo, IFileSystemFile
{
    private readonly FileInfo _fileInfo;
    private readonly PhysicalFileSystemDirectory _directory;

    public PhysicalFileSystemFile(PhysicalFileSystem fileSystem, FileInfo fileInfo)
        : base(fileSystem, fileInfo)
    {
        _fileInfo = fileInfo;
        _directory = new PhysicalFileSystemDirectory(
            fileSystem,
            _fileInfo.Directory!);
    }

    public FileName Name => _fileInfo.Name;
    public Size Size
    {
        get
        {
            // Refresh so the reported length reflects subsequent writes through Open().
            _fileInfo.Refresh();
            return _fileInfo.Length;
        }
    }
    public PhysicalFileSystemDirectory Directory => _directory;
    IFileSystemDirectory IFileSystemFile.Directory => _directory;
    public IFileSystemEventToken Watch()
    {
        string path = Path;

        return new PhysicalFileSystemChangeToken(
            this,
            path);
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
        if (FileSystem.IsReadOnly && (fileMode != FileMode.Open || fileAccess != FileAccess.Read))
        {
            throw new InvalidOperationException("The file system is read-only. Only FileMode.Open with FileAccess.Read is allowed.");
        }

        return File.Open(Path, fileMode, fileAccess, fileShare);
    }
    public override void Dispose()
    {

    }
}
