
using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections;

namespace Assimalign.Cohesion.FileSystem.Internal;

[DebuggerDisplay("d - {Path}")]
internal class InMemoryFileSystemDirectory : InMemoryFileSystemInfo, IFileSystemDirectory
{
    public InMemoryFileSystemDirectory()
    {
        Children = new List<InMemoryFileSystemInfo>();
    }
    public InMemoryFileSystemDirectory(DirectoryName name) 
        : this()
    {
        Name = name;
    }
    public InMemoryFileSystemDirectory(DirectoryName name, InMemoryFileSystemDirectory parent) 
        : this(name)
    {
        Parent = parent;
    }

    public DirectoryName Name { get; } = DirectoryName.Empty;
    public InMemoryFileSystemDirectory? Parent { get; }
    public List<InMemoryFileSystemInfo> Children { get; }
    IFileSystemDirectory? IFileSystemDirectory.Parent => Parent;

    public void Copy(FileSystemPath source, FileSystemPath destination)
    {
        BeginSharedLock(FileShare.Read);

        try
        {
            if (source == destination)
            {
                // TODO: throw exception
            }
        }
        finally
        {
            EndSharedLock();
        }
    }

    public void Move(FileSystemPath source, FileSystemPath destination)
    {
        throw new NotImplementedException();
    }

    public IFileSystemDirectory CreateDirectory(FileSystemPath path)
    {
        BeginSharedLock(FileShare.Read);

        try
        {
            InMemoryFileSystemDirectory parent = this;

            var values = Path.Combine(path).GetDirectoryNames();

            for (int i = 0; i < values.Length; i++)
            {
                var isFound = false;
                var name = values[i];
                var children = parent.Children;

                // Check if starting from root
                if (Comparer.Equals(name, parent.Name))
                {
                    continue;
                }

                for (int a = 0; a < children.Count; a++)
                {
                    switch (children[a])
                    {
                        case InMemoryFileSystemFile file
                        when Comparer.Equals(file.Name, name):
                            {
                                throw new IOException($"Cannot create directory `{path}` on an existing file");
                            }
                        case InMemoryFileSystemDirectory directory
                        when (isFound = Comparer.Equals(directory.Name, name)):
                            {
                                parent = directory;
                                parent.AccessedOn = DateTime.Now;
                                break;
                            }
                    }
                }

                // If the sub directory does not exist, then create
                if (!isFound)
                {
                    children.Add((parent = new InMemoryFileSystemDirectory(name, parent)
                    {
                        FileSystem = base.FileSystem,
                        Comparer = base.Comparer,
                    }));

                    parent.Parent!.UpdatedOn = DateTime.Now;
                }
            }

            return parent;
        }
        finally
        {
            EndSharedLock();
        }
    }
    public IFileSystemFile CreateFile(FileSystemPath path)
    {
        try
        {
            BeginSharedLock(FileShare.Read);




            return default!;
        }
        finally
        {
            EndSharedLock();
        }
    }

    public void DeleteDirectory(FileSystemPath path)
    {
        BeginSharedLock(FileShare.Read);

        try
        {
            InMemoryFileSystemDirectory parent = this;

            var values = Path.Combine(path).GetDirectoryNames();

            for (int i = 0; i < values.Length; i++)
            {
                var isFound = false;
                var name = values[i];
                var children = parent.Children;

                for (int a = 0; a < children.Count; a++)
                {
                    switch (children[a])
                    {
                        case InMemoryFileSystemFile file
                        when Comparer.Equals(file.Name, name):
                            {
                                throw new IOException($"Cannot create directory `{path}` on an existing file");
                            }
                        case InMemoryFileSystemDirectory directory
                        when (isFound = Comparer.Equals(directory.Name, name)):
                            {
                                parent = directory;
                                break;
                            }
                    }
                }

                // If sub directory does not exist create
                if (parent is null)
                {
                    throw new Exception();
                    // TODO: throw not found exception
                }
            }

            if (parent.IsLocked)
            {
                throw new UnauthorizedAccessException();
            }

            parent.Dispose();
        }
        finally
        {
            EndSharedLock();
        }
    }

    public void DeleteFile(FileSystemPath path)
    {
        throw new NotImplementedException();
    }

    public bool Exist(FileSystemPath path)
    {
        throw new NotImplementedException();
    }

    public IEnumerable<IFileSystemDirectory> GetDirectories()
    {
        return Children.OfType<InMemoryFileSystemDirectory>();
    }

    public IFileSystemDirectory GetDirectory(FileSystemPath path)
    {
        BeginSharedLock(FileShare.Read);

        try
        {
            InMemoryFileSystemDirectory parent = this;

            var names = path.GetDirectoryNames();

            for (int i = 0; i < names.Length; i++)
            {
                var name = names[i];
                var directory = Children
                    .OfType<InMemoryFileSystemDirectory>()
                    .FirstOrDefault(p => Comparer.Equals(p.Name, name));

                if (directory is null) 
                {
                    throw new Exception();
                }
                else
                {
                    parent = directory;
                }
            }

            return parent;
        }
        finally
        {
            EndSharedLock();
        }
    }

    public IEnumerator<IFileSystemInfo> GetEnumerator()
    {
        return Children.GetEnumerator();
    }

    public IFileSystemFile GetFile(FileSystemPath path)
    {
        throw new NotImplementedException();
    }

    public IEnumerable<IFileSystemFile> GetFiles()
    {
        return Children.OfType<InMemoryFileSystemFile>();
    }

    public IFileSystemChangeToken Watch(string filter)
    {
        // TODO: Apply filtering
        return GetToken(this);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public override void Dispose()
    {
        // Create exclusive lock
        BeginLock();

        try
        {
            if (Parent is not null)
            {
                Parent.Children.Remove(this);
            }

            foreach (var child in Children)
            {
                child.Dispose();
            }
        }
        finally
        {
            Endlock();
        }
    }
}
