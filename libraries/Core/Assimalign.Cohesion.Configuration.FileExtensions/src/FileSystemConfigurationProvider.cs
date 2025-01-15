using Assimalign.Cohesion.FileSystem;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

internal abstract class FileSystemConfigurationProvider : IConfigurationProvider
{
    private readonly IFileSystem fileSystem;

    public FileSystemConfigurationProvider(IFileSystem fileSystem, FileSystemPath path)
    {
        this.fileSystem = fileSystem;

        Name = path.GetFileName()!;
    }
    public string Name { get; }

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public IEnumerable<IConfigurationEntry> EnumerateEntries()
    {
        throw new NotImplementedException();
    }

    public object Get(Key key)
    {
        throw new NotImplementedException();
    }
    public void Set(Key key, object value)
    {
        throw new NotImplementedException();
    }

    public void Load()
    {
        throw new NotImplementedException();
    }

    public Task LoadAsync()
    {
        throw new NotImplementedException();
    }

    public void Reload()
    {
        throw new NotImplementedException();
    }

    public Task RefreshAsync()
    {
        throw new NotImplementedException();
    }

    
}
