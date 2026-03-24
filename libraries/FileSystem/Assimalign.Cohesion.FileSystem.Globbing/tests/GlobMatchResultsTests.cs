using System;
using System.Linq;

namespace Assimalign.Cohesion.FileSystem.Globbing.Tests;

public class GlobMatchResultsTests
{
    [Fact]
    public void Constructor_WithFiles_StoresFiles()
    {
        var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions
        {
            RootPath = "/test"
        });

        var file1 = fileSystem.CreateFile("/test/file1.txt");
        var file2 = fileSystem.CreateFile("/test/file2.txt");
        var files = new[] { file1, file2 };

        var results = new GlobMatchResults(files);

        Assert.Equal(2, results.Files.Count());
        Assert.Contains(file1, results.Files);
        Assert.Contains(file2, results.Files);
    }

    [Fact]
    public void Constructor_WithNullFiles_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new GlobMatchResults(null!));
    }

    [Fact]
    public void HasMatches_WithFiles_ReturnsTrue()
    {
        var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions
        {
            RootPath = "/test"
        });

        var file = fileSystem.CreateFile("/test/file.txt");
        var results = new GlobMatchResults(new[] { file });

        Assert.True(results.HasMatches);
    }

    [Fact]
    public void HasMatches_WithEmptyFiles_ReturnsFalse()
    {
        var results = new GlobMatchResults(Array.Empty<IFileSystemInfo>());

        Assert.False(results.HasMatches);
    }

    [Fact]
    public void Empty_ReturnsEmptyResults()
    {
        var results = GlobMatchResults.Empty;

        Assert.False(results.HasMatches);
        Assert.Empty(results.Files);
    }

    [Fact]
    public void Empty_IsSingleton()
    {
        var empty1 = GlobMatchResults.Empty;
        var empty2 = GlobMatchResults.Empty;

        Assert.Same(empty1, empty2);
    }

    [Fact]
    public void Files_CanBeEnumerated()
    {
        var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions
        {
            RootPath = "/test"
        });

        var file1 = fileSystem.CreateFile("/test/file1.txt");
        var file2 = fileSystem.CreateFile("/test/file2.txt");
        var file3 = fileSystem.CreateFile("/test/file3.txt");
        var files = new[] { file1, file2, file3 };

        var results = new GlobMatchResults(files);

        var count = 0;
        foreach (var file in results.Files)
        {
            count++;
            Assert.NotNull(file);
        }

        Assert.Equal(3, count);
    }

    [Fact]
    public void Files_IncludesDirectories_WhenNotExcluded()
    {
        var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions
        {
            RootPath = "/test"
        });

        var dir = fileSystem.CreateDirectory("/test/folder");
        var file = fileSystem.CreateFile("/test/file.txt");
        var items = new IFileSystemInfo[] { dir, file };

        var results = new GlobMatchResults(items);

        Assert.Equal(2, results.Files.Count());
        Assert.Contains(dir, results.Files);
        Assert.Contains(file, results.Files);
    }
}
