using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal class FsDirectoryNode : FsNode
{
    internal Dictionary<Path, FsNode> _children;

    public FsDirectoryNode(InMemoryFileSystem fileSystem) : base(fileSystem, null, null, null)
    {
        _children = new Dictionary<Path, FsNode>();
        Attributes = FileAttributes.Directory;
    }
    public FsDirectoryNode(InMemoryFileSystem fileSystem, FsDirectoryNode parent, string name) : base(fileSystem, parent, name, null)
    {
        Debug.Assert(parent != null);
        _children = new Dictionary<Path, FsNode>();
        Attributes = FileAttributes.Directory;
    }
    public Dictionary<Path, FsNode> Children
    {
        get
        {
            Debug.Assert(IsLocked);
            return _children;
        }
    }
    public override FsNode Clone(FsDirectoryNode? newParent, string? newName)
    {
        var dir = (FsDirectoryNode)base.Clone(newParent, newName);
        dir._children = new Dictionary<Path, FsNode>();
        foreach (var name in _children.Keys)
        {
            dir._children[name] = _children[name].Clone(dir, name);
        }
        return dir;
    }
}
