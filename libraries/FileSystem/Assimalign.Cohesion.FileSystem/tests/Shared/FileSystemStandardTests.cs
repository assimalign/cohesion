using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.FileSystem.Tests;

public abstract class FileSystemStandardTests
{
    public abstract IFileSystem GetFileSystem();


    [Fact]
    public void TestFileAlreadyExistException()
    {
        var fileSystem = GetFileSystem();

        Assert.NotNull(fileSystem.CreateFile("test.txt"));

        var exception = Assert.Throws<FileSystemException>(() =>
        {
            fileSystem.CreateFile("test.txt");
        });

        Assert.Equal(FileSystemErrorCode.Conflict, exception.Code);
    }

    [Fact]
    public void TestDirectoryAlreadyExistException()
    {
        var fileSystem = GetFileSystem();
        
        Assert.NotNull(fileSystem.CreateDirectory("test"));

        var exception = Assert.Throws<FileSystemException>(() =>
        {
            fileSystem.CreateDirectory("test");
        });

        Assert.Equal(FileSystemErrorCode.Conflict, exception.Code);
    }
}
