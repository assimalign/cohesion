using System.IO;

namespace Assimalign.Extensions.FileSystemGlobbing;


public sealed class InMemoryFileInfo : IFileComponent
{
    private InMemoryFileDirectoryInfo parent;

    public InMemoryFileInfo(string file, InMemoryFileDirectoryInfo parent)
    {
        FullName = file;
        Name = Path.GetFileName(file);
        this.parent = parent;
    }

    public string FullName { get; }

    public string Name { get; }

    public InMemoryFileDirectoryInfo ParentDirectory => parent;

    IFileComponentContainer IFileComponent.ParentComponent => this.ParentDirectory;
}
