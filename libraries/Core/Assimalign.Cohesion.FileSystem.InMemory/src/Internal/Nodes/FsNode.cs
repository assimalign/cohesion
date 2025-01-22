using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Enumeration;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem.Internal;

using Assimalign.Cohesion.Internal;

internal class FsNode : FsNodeLock
{
    protected readonly InMemoryFileSystem FileSystem;

    protected FsNode(
        InMemoryFileSystem fileSystem, 
        FsDirectoryNode? parentNode, 
        string? name, 
        FsNode? copyNode)
    {
        Debug.Assert((parentNode is null) == string.IsNullOrEmpty(name));
        
        if (FileSystem is null)
        {
            ThrowHelper.ThrowArgumentNullException(nameof(fileSystem));
        }
        
        FileSystem = fileSystem;

        if (parentNode != null && name is { Length: > 0 })
        {
            Debug.Assert(parentNode.IsLocked);
            parentNode.Children.Add(name, this);
            Parent = parentNode;
            Name = name;
        }
        if (copyNode != null && copyNode.Attributes != 0)
        {
            Attributes = copyNode.Attributes;
        }
        CreationTime = DateTime.Now;
        LastWriteTime = copyNode?.LastWriteTime ?? CreationTime;
        LastAccessTime = copyNode?.LastAccessTime ?? CreationTime;
    }


    public string? Name { get; private set; }
    public FsDirectoryNode? Parent { get; private set; }
    public FileAttributes Attributes { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime LastWriteTime { get; set; }
    public DateTime LastAccessTime { get; set; }
    public bool IsDisposed { get; set; }
    public bool IsReadOnly => (Attributes & FileAttributes.ReadOnly) != 0;
    public void DetachFromParent()
    {
        Debug.Assert(IsLocked);
        var parent = Parent!;
        Debug.Assert(parent.IsLocked);
        parent.Children.Remove(Name!);
        Parent = null!;
        Name = null!;
    }
    public void AttachToParent(FsDirectoryNode parentNode, string name)
    {
        if (parentNode is null)
            throw new ArgumentNullException(nameof(parentNode));
        if (string.IsNullOrEmpty(name))
            throw new ArgumentNullException(nameof(name));
        Debug.Assert(parentNode.IsLocked);
        Debug.Assert(IsLocked);
        Debug.Assert(Parent is null);
        Parent = parentNode;
        Parent.Children.Add(name, this);
        Name = name;
    }
    public void Dispose()
    {
        Debug.Assert(IsLocked);
        // In order to issue a Dispose, we need to have control on this node
        IsDisposed = true;
    }
    public virtual FsNode Clone(FsDirectoryNode? newParent, string? newName)
    {
        Debug.Assert((newParent is null) == string.IsNullOrEmpty(newName));
        var clone = (FsNode)Clone();
        clone.Parent = newParent;
        clone.Name = newName;
        return clone;
    }
}
