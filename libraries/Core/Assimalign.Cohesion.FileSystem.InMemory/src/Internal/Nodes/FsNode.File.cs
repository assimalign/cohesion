namespace Assimalign.Cohesion.FileSystem.Internal;

internal class FsFileNode : FsNode
{
    public FsFileNode(
        InMemoryFileSystem fileSystem,
        FsDirectoryNode parentNode,
        string? name,
        FsFileNode? copyNode) : base(fileSystem, parentNode, name, copyNode)
    {
        if (copyNode != null)
        {
            Content = new InMemoryFileContent(this, copyNode.Content);
        }
        else
        {
            // Mimic OS-specific attributes.
            //Attributes = PhysicalFileSystem.IsOnWindows ? FileAttributes.Archive : FileAttributes.Normal;
            Content = new InMemoryFileContent(this);
        }
    }
    public InMemoryFileContent Content { get; private set; }
    public override FsNode Clone(FsDirectoryNode? newParent, string? newName)
    {
        var copy = (FsFileNode)base.Clone(newParent, newName);
        copy.Content = new InMemoryFileContent(copy, Content);
        return copy;
    }

    public void ContentChanged()
    {
        //var dispatcher = FileSystem.TryGetDispatcher();
        //if (dispatcher != null)
        //{
        //    // TODO: cache this
        //    var path = GeneratePath();
        //    dispatcher.RaiseChange(path);
        //}
    }
    private Path GeneratePath()
    {
        //var builder = UPath.GetSharedStringBuilder();
        //FsFileNode node = this;
        //var parent = Parent;
        //while (parent != null)
        //{
        //    builder.Insert(0, node.Name);
        //    builder.Insert(0, UPath.DirectorySeparator);
        //    node = parent;
        //    parent = parent.Parent;
        //}
        //return builder.ToString();

        return default;
    }
}
