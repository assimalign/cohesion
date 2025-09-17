using System;
using System.IO;
using System.Linq;

namespace Assimalign.Cohesion.FileSystem.Tests;

public class UnitTest1
{
    [Fact]
    public void CreateDirectoryTest()
    {

        FileSystemPath path = Guid.NewGuid().ToString();
        IFileSystemFactory fileSystemFactory = new FileSystemFactoryBuilder()
           .AddInMemoryFileSystem(options =>
           {
               options.Comparison = StringComparison.OrdinalIgnoreCase;
           })
           .Build();
        Assert.NotNull(fileSystemFactory);

        var fileSystem = fileSystemFactory.Create("InMemoryFileSystem");
        var dir1 = fileSystem.CreateDirectory("dir1/dir1.1");
        var dir2 = fileSystem.CreateDirectory("dir1/dir1.2");
        var dir3 = fileSystem.CreateDirectory("dir1/dir1.3");
        var dir4 = fileSystem.CreateDirectory("dir1/dir1.3/dir1.3.1");
        var dir5 = fileSystem.CreateDirectory("dir1/dir1.3/dir1.3.2");




    }
}
