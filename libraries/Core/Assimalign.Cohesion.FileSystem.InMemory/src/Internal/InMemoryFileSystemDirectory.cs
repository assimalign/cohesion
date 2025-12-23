using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Assimalign.Cohesion.FileSystem.Internal;

using Assimalign.Cohesion.Internal;

[DebuggerDisplay("[D] - {Path}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal class InMemoryFileSystemDirectory : InMemoryFileSystemInfo, IFileSystemDirectory
{
    private readonly Dictionary<FileSystemPath, InMemoryFileSystemInfo> _entries;
    private readonly Dictionary<FileSystemPath, InMemoryFileSystemInfo>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    private InMemoryFileSystemDirectory? _parent;
    private DirectoryName _name;
    private bool _isDiposed;

    InMemoryFileSystemDirectory(InMemoryFileSystem fileSystem, CultureInfo cultureInfo, bool ignoreCase)
        : base(fileSystem, cultureInfo, ignoreCase)
    {
        _entries = new Dictionary<FileSystemPath, InMemoryFileSystemInfo>(FileSystemPathComparer.Create(cultureInfo, ignoreCase));
        _lookup = _entries.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public InMemoryFileSystemDirectory(DirectoryName name, InMemoryFileSystemDirectory parent, InMemoryFileSystem fileSystem)
        : this(name, fileSystem, parent.CultureInfo, parent.IgnoreCase)
    {
        _parent = parent;
    }

    public InMemoryFileSystemDirectory(DirectoryName name, InMemoryFileSystem fileSystem, CultureInfo cultureInfo, bool ignoreCase)
        : this(fileSystem, cultureInfo, ignoreCase)
    {
        _name = name;
    }

    public InMemoryFileSystemDirectory(FileSystemPath path, InMemoryFileSystem fileSystem, CultureInfo cultureInfo, bool ignoreCase)
        : this(fileSystem, cultureInfo, ignoreCase)
    {
        DirectoryName[] names = path.GetDirectoryNames();

        if (names.Length > 1)
        {
            _name = names[^1];
            _parent = new InMemoryFileSystemDirectory(
                FileSystemPath.Create(names[..^1]),
                fileSystem,
                cultureInfo,
                ignoreCase);
        }
        else
        {
            _name = names[0];
        }
    }

    public int Count => _entries.Count;
    public Dictionary<FileSystemPath, InMemoryFileSystemInfo> Entries => _entries;
    public Dictionary<FileSystemPath, InMemoryFileSystemInfo>.AlternateLookup<ReadOnlySpan<char>> Lookup => _lookup;
    public DirectoryName Name => _name;
    public InMemoryFileSystemDirectory? Parent => _parent;
    IFileSystemDirectory? IFileSystemDirectory.Parent => Parent;

    public IFileSystemEventToken Watch(Glob? glob)
    {
        return new InMemoryFileSystemEventToken(
            this,
            glob ?? Glob.Parse(Path));
    }

    public IFileSystemDirectory CreateDirectory(DirectoryName name)
    {
        return FileSystem.CreateDirectory(Path.Join(name));
    }

    public IFileSystemFile CreateFile(FileName name)
    {
        return FileSystem.CreateFile(Path.Join(name));
    }

    public void DeleteDirectory(DirectoryName name)
    {
        FileSystem.DeleteDirectory(Path.Join(name));
    }

    public void DeleteFile(FileName name)
    {
        FileSystem.DeleteFile(Path.Join(name));
    }

    public IFileSystemDirectory GetDirectory(DirectoryName name)
    {
        return FileSystem.GetDirectory(Path.Join(name));
    }

    public IFileSystemFile GetFile(FileName name)
    {
        return FileSystem.GetFile(Path.Join(name));
    }

    public IEnumerable<IFileSystemDirectory> GetDirectories()
    {
        return EnumerateFileSystem().OfType<InMemoryFileSystemDirectory>();
    }

    public IEnumerable<IFileSystemFile> GetFiles()
    {
        return EnumerateFileSystem().OfType<InMemoryFileSystemFile>();
    }

    public IEnumerable<IFileSystemInfo> EnumerateFileSystem(FileSystemEnumerationOptions? options = null)
    {
        Lock(LockPolicy.Delete);

        try
        {
            options ??= new FileSystemEnumerationOptions()
            {
                Path = Path,
                Recurse = false
            };

            if (options.Path is null)
            {
                options.Path = Path;
            }

            if (options.Recurse)
            {
                return _entries.Values
                    .Where(item => item.Path.StartsWith(options.Path.Value, CultureInfo, IgnoreCase))
                    .SelectMany(item => item switch
                    {
                        InMemoryFileSystemDirectory dir => dir.EnumerateFileSystem(new FileSystemEnumerationOptions()
                        {
                            Recurse = true
                        }),
                        InMemoryFileSystemFile file => new InMemoryFileSystemFile[] { file },
                        _ => Array.Empty<IFileSystemInfo>()
                    });
            }

            return _entries.Values.Where(item => item.Path.StartsWith(options.Path.Value, CultureInfo, IgnoreCase));
        }
        finally
        {
            Unlock();
        }
    }

    public IEnumerator<IFileSystemInfo> GetEnumerator()
    {
        return EnumerateFileSystem().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public override void Dispose()
    {
        ObjectDisposedException.ThrowIf(_isDiposed, this);

        Lock(LockPolicy.Exclusive);

        try
        {
            if (_parent is not null && _parent.IsLocked)
            {
                // TODO: Need to go through code path to see if child ever needs to lock parent
            }

            foreach (var (key, entry) in _entries)
            {
                entry.Dispose();
            }

            if (_parent is not null)
            {
                _parent.Entries.Remove(Path);
            }

            _isDiposed = true;
        }
        finally
        {
            Unlock();

            base.Dispose();

            GC.SuppressFinalize(this);
        }
    }



    private sealed class DebugView
    {
        private readonly InMemoryFileSystemDirectory _directory;
        public DebugView(InMemoryFileSystemDirectory directory)
        {
            _directory = directory;
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
        public InMemoryFileSystemInfo[] Entries => _directory.Cast<InMemoryFileSystemInfo>().ToArray();
        public FileSystemPath Path => _directory.Path;
        public DirectoryName Name => _directory.Name;
        public InMemoryFileSystemDirectory? Parent => _directory.Parent;
    }
}
