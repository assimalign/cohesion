using System;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;

namespace Assimalign.Cohesion.FileSystem.Internal;

using Assimalign.Cohesion.Internal;

[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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
    /// <inheritdoc />
    public IFileSystemFileHandle OpenHandle(FileMode fileMode, FileAccess fileAccess, FileShare fileShare)
    {
        if (FileSystem.IsReadOnly && (fileMode != FileMode.Open || fileAccess != FileAccess.Read))
        {
            throw new InvalidOperationException("The file system is read-only. Only FileMode.Open with FileAccess.Read is allowed.");
        }

        return new PhysicalFileSystemFileHandle(File.OpenHandle(
            Path, fileMode, fileAccess, fileShare, FileOptions.Asynchronous | FileOptions.RandomAccess));
    }
    public override void Dispose()
    {

    }

    private sealed class DebugView
    {
        private readonly PhysicalFileSystemFile _target;

        public DebugView(PhysicalFileSystemFile value)
        {
            _target = value;
        }

        public FileName Name => _target.Name;
        public PhysicalFileSystemDirectory Directory => _target.Directory;
        public global::System.IO.FileSystemPath Path => _target.Path;
        public global::System.DateTime UpdatedOn => _target.UpdatedOn;
        public global::System.DateTime CreatedOn => _target.CreatedOn;
        public global::System.DateTime AccessedOn => _target.AccessedOn;
        public global::Assimalign.Cohesion.FileSystem.PhysicalFileSystem FileSystem => _target.FileSystem;
        public global::System.IO.FileAttributes Attributes => _target.Attributes;
        public global::System.IO.FileAttributes IgnoreAttributes => _target.IgnoreAttributes;
    }
}
