using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Assimalign.Cohesion.FileSystem.Tests;

public class FileSystemTests : FileSystemStandardTests
{

    public override IFileSystem GetFileSystem()
    {
        IFileSystemFactory fileSystemFactory = new FileSystemFactoryBuilder()
          .AddInMemoryFileSystem(options =>
          {
              options.Size = Size.FromGigabytes(1);
              options.RootPath = "//share/users/chase";
              // options.CultureInfo = StringComparison.OrdinalIgnoreCase;
          })
          .Build();

        return fileSystemFactory.Create("InMemoryFileSystem");
    }

    [Fact]
    public void CreateDirectoryTest()
    {
        IFileSystem fileSystem = GetFileSystem();

        int depth = 1;

        foreach (var path in GetDirectories("C:\\Users\\chase", 0, depth))
        {
            fileSystem.CreateDirectory(path.Replace("C:\\", ""));

            bool exists = fileSystem.Exists(path.Replace("C:\\", ""));
        }

        fileSystem.Dispose();

        foreach (var path in GetFiles("C:\\Users\\chase", 0, depth))
        {
            var file = fileSystem.CreateFile(path.Replace("C:\\", ""));

            fileSystem.DeleteFile(file.Path);

            //using var stream = file.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

            //var content = File.ReadAllBytes(path);

            //stream.Write(content);
        }




        //Directory.EnumerateDirectories();

        //Directory.EnumerateFileSystemEntries("*")
        //Assert.NotNull(fileSystemFactory);

        //var fileSystem = fileSystemFactory.Create("InMemoryFileSystem");
        //var dir1 = fileSystem.CreateDirectory("dir1/dir1.1");
        //var dir2 = fileSystem.CreateDirectory("dir1/dir1.2");
        //var file = fileSystem.CreateFile("dir1/dir1.2/test.txt");
        ////var same = fileSystem.CreateDirectory("dir1/dir1.2/test.txt");
        //var dir3 = fileSystem.CreateDirectory("dir1/dir1.3");
        //var dir4 = fileSystem.CreateDirectory("dir1/dir1.3/dir1.3.1");
        //var dir5 = fileSystem.CreateDirectory("dir1/dir1.3/dir1.3.2");

        //using var stream = file.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        //var buffer = Encoding.UTF8.GetBytes("This is a test");

        //stream.Write(buffer, 0, buffer.Length);


    }



    public IEnumerable<string> GetDirectories(string path, int index, int depth)
    {
        foreach (var dir in Directory.EnumerateDirectories(path, "*", new EnumerationOptions()
        {
            IgnoreInaccessible = true
        }))
        {
            yield return dir;

            if (index < depth)
            {
                foreach (var child in GetDirectories(dir, index + 1, depth))
                {
                    yield return child;
                }
            }
        }
    }

    public IEnumerable<string> GetFiles(string path, int index, int depth)
    {
        return Directory.EnumerateFiles(path, "*", new EnumerationOptions()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MaxRecursionDepth = depth
        });
    }

    
}
