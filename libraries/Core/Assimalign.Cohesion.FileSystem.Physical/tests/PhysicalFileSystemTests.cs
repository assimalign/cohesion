namespace Assimalign.Cohesion.FileSystem.Physical.Tests;


using FileSystem.Globbing;
using Xunit;

public class PhysicalFileSystemTests
{
    [Fact]
    public void TestCreateDirectorySuccess()
    {
        FileSystemPath path = Guid.NewGuid().ToString();
        IFileSystemFactory fileSystemFactory = new FileSystemFactoryBuilder()
           .AddPhysicalFileSystem(options =>
           {
               options.Root = Directory.GetCurrentDirectory();
           })
           .Build();
        Assert.NotNull(fileSystemFactory);


        IFileSystem fileSystem = fileSystemFactory.Create("PhysicalFileSystem");
        Assert.NotNull(fileSystem);

        IFileSystemDirectory directory = fileSystem.CreateDirectory(path);
        Assert.True(Directory.Exists(directory.Path));

        fileSystem.DeleteDirectory(path);
        Assert.False(Directory.Exists(directory.Path));
    }



    [Fact]
    public void TestReadOnlyException()
    {
        IFileSystemFactory fileSystemFactory = new FileSystemFactoryBuilder()
           .AddPhysicalFileSystem(options =>
           {
               options.IsReadOnly = true;
               options.Root = Directory.GetCurrentDirectory();
           })
           .Build();

        Assert.NotNull(fileSystemFactory);

        IFileSystem fileSystem = fileSystemFactory.Create("PhysicalFileSystem");

        Assert.NotNull(fileSystem);

        FileSystemPath directory = $"C://{Guid.NewGuid()}";
        FileSystemPath file = $"{directory}/file.txt";

        Assert.Throws<InvalidOperationException>(() =>
        {
            fileSystem.CreateDirectory(directory);
        });

        Assert.Throws<InvalidOperationException>(() =>
        {
            fileSystem.CreateFile(file);
        });

        Assert.Throws<InvalidOperationException>(() =>
        {
            fileSystem.DeleteDirectory(directory);
        });

        Assert.Throws<InvalidOperationException>(() =>
        {
            fileSystem.DeleteFile(file);
        });

    }
}
