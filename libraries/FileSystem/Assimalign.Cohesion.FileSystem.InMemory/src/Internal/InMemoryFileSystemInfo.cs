using System;
using System.IO;
using System.Globalization;

namespace Assimalign.Cohesion.FileSystem.Internal;

[Flags]
internal enum LockPolicy
{
    Unlocked = 0,
    Read = 1,
    Write = 2,
    Delete = 4,
    Exclusive = Read | Write | Delete
}


internal abstract class InMemoryFileSystemInfo : InMemoryFileSystemLockHandle, IFileSystemInfo, IDisposable
{
    private readonly InMemoryFileSystem _fileSystem;
    private readonly DateTime _createdOn;
    private readonly CultureInfo _cultureInfo;
    private readonly bool _ignoreCase;
    private DateTime _accessedOn;
    private DateTime _updatedOn;
    private FileAttributes _attributes;
    private Lazy<InMemoryFileSystemDispatcher> _dispatcher;

    protected InMemoryFileSystemInfo(InMemoryFileSystem fileSystem, CultureInfo cultureInfo, bool ignoreCase)
    {
        _fileSystem = fileSystem;
        _createdOn = DateTime.Now;
        _accessedOn = _createdOn;
        _updatedOn = _createdOn;
        _cultureInfo = cultureInfo;
        _ignoreCase = ignoreCase;
        _dispatcher = new Lazy<InMemoryFileSystemDispatcher>(() => this switch
        {
            InMemoryFileSystemFile file => new InMemoryFileSystemDispatcher(file.Directory.Dispatcher),
            InMemoryFileSystemDirectory dir when dir.Parent is not null => new InMemoryFileSystemDispatcher(dir.Parent.Dispatcher),
            _ => new InMemoryFileSystemDispatcher()
        });
    }

    public DateTime UpdatedOn => _updatedOn;
    public DateTime AccessedOn => _accessedOn;
    public DateTime CreatedOn => _createdOn;
    public FileSystemPath Path => GetPath(this);
    public FileAttributes Attributes => _attributes;
    public FileAttributes IgnoreAttributes { get; init; }
    public bool IgnoreCase => _ignoreCase;
    public CultureInfo CultureInfo => _cultureInfo;
    public InMemoryFileSystem FileSystem => _fileSystem;
    public InMemoryFileSystemDispatcher Dispatcher => _dispatcher.Value;
    IFileSystem IFileSystemInfo.FileSystem => FileSystem;
    public void SetAccessedOn(DateTime accessedOn) => _accessedOn = accessedOn;
    public void SetUpdatedOn(DateTime updatedOn) => _updatedOn = updatedOn;
    public void SetAttributes(FileAttributes attributes) => _attributes = attributes;
    public virtual void Dispose()
    {
        Dispatcher.Dispose();
    }
    private FileSystemPath GetPath(InMemoryFileSystemInfo info)
    {
        return info switch
        {
            InMemoryFileSystemDirectory directory => GetPath(directory),
            InMemoryFileSystemFile file => GetPath(file),
            _ => throw new Exception()
        };
    }
    private FileSystemPath GetPath(InMemoryFileSystemDirectory directory)
    {
        if (directory.HasParent<InMemoryFileSystemDirectory>(out var parent))
        {
            return FileSystemPath.Merge(GetPath((InMemoryFileSystemDirectory)parent), directory.Name);
        }

        return directory.Name;
    }
    private FileSystemPath GetPath(InMemoryFileSystemFile file)
    {
        return FileSystemPath.Merge(GetPath(file.Directory), file.Name);
    }
}
