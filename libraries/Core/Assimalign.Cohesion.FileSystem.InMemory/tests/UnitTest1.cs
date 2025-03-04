using System.IO;
using System.Linq;

namespace Assimalign.Cohesion.FileSystem.Tests;

public class UnitTest1
{
    [Theory]
    [InlineData(true, "$root", "/my/custom/path", "$root/my/Custom/path", 1)]
    [InlineData(false, "$root", "/my/custom/path", "$root/my/Custom/path", 2)]
    [InlineData(false, "", "/my/custom/path", "/my/Custom/path", 2)]
    [InlineData(true, "", "/my/custom/path", "/my/Custom/path", 1)]
    public void CreateDirectoryTest(bool ignoreCase, string root, string path1, string path2, int count)
    {
        var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions()
        {
            IgnoreCase = ignoreCase,
            RootName = root
        });

        var dir1 = fileSystem.CreateDirectory(path1);
        var dir2 = fileSystem.CreateDirectory(path2);

        // Assert `/my` is the same reference
        Assert.True(ReferenceEquals(dir1.Parent!.Parent, dir2.Parent!.Parent));

        var parent = dir1.Parent!.Parent!;
        var children = parent.ToList();

        // Assert the expected child dir count
        Assert.Equal(count, children.Count);


        
    }
}
