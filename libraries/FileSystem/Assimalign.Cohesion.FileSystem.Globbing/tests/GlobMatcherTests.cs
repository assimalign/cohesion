using System;
using System.IO;
using System.Linq;

namespace Assimalign.Cohesion.FileSystem.Globbing.Tests;

public class GlobMatcherTests
{
    [Theory]
    [InlineData("**/*.txt", "/file.txt", true)]
    [InlineData("*.txt", "/file.log", false)]
    [InlineData("**/*.txt", "folder/file.txt", true)]
    [InlineData("**/*.txt", "folder/subfolder/file.txt", true)]
    [InlineData("src/**/*.cs", "src/Program.cs", true)]
    [InlineData("**/src/**/*.cs", "/src/Models/User.cs", true)]
    //[InlineData("src/**/*.cs", "/test/Program.cs", false)]
    public void IsMatch_WithPath_ReturnsExpectedResult(string pattern, string path, bool expected)
    {
        var builder = new GlobMatcherBuilder();
        builder.AddInclude(Glob.Parse(pattern));
        var matcher = builder.Build();

        var result = matcher.IsMatch(FileSystemPath.Parse(path));

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("*.TXT", "file.txt", true)]
    [InlineData("*.txt", "file.TXT", true)]
    //[InlineData("SRC/**/*.cs", "src/Program.cs", true)]
    public void IsMatch_WithIgnoreCase_MatchesCaseInsensitively(string pattern, string path, bool expected)
    {
        var options = new GlobMatcherOptions() 
        {
            IgnoreCase = true 
        };
        var builder = new GlobMatcherBuilder(options);
        builder.AddInclude(Glob.Parse(pattern));
        var matcher = builder.Build();

        var result = matcher.IsMatch(FileSystemPath.Parse(path));

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("*.TXT", "/file.txt", false)]
    [InlineData("*.txt", "/file.TXT", false)]
    public void IsMatch_WithCaseSensitive_MatchesCaseSensitively(string pattern, string path, bool expected)
    {
        var options = new GlobMatcherOptions { IgnoreCase = false };
        var builder = new GlobMatcherBuilder(options);
        builder.AddInclude(Glob.Parse(pattern));
        var matcher = builder.Build();

        var result = matcher.IsMatch(FileSystemPath.Parse(path));

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsMatch_WithExcludePattern_ExcludesMatchingPaths()
    {
        var builder = new GlobMatcherBuilder();
        builder.AddInclude(Glob.Parse("/*.txt"))
               .AddExclude(Glob.Parse("/temp.txt"));
        var matcher = builder.Build();

        var result1 = matcher.IsMatch("/file.txt");
        var result2 = matcher.IsMatch("/temp.txt");

        Assert.True(result1);
        Assert.False(result2);
    }

    [Fact]
    public void IsMatch_WithMultipleIncludes_MatchesAnyPattern()
    {
        var builder = new GlobMatcherBuilder();
        builder.AddInclude(Glob.Parse("*.txt"))
               .AddInclude(Glob.Parse("*.md"));
        var matcher = builder.Build();

        Assert.True(matcher.IsMatch(FileSystemPath.Parse("file.txt")));
        Assert.True(matcher.IsMatch(FileSystemPath.Parse("readme.md")));
        Assert.False(matcher.IsMatch(FileSystemPath.Parse("file.log")));
    }

    [Fact]
    public void IsMatch_WithMultipleExcludes_ExcludesAllMatchingPatterns()
    {
        var builder = new GlobMatcherBuilder();

        builder.AddInclude(Glob.Parse("*.*"))
               .AddExclude(Glob.Parse("*.log"))
               .AddExclude(Glob.Parse("*.tmp"));

        var matcher = builder.Build();

        Assert.True(matcher.IsMatch(FileSystemPath.Parse("file.txt")));
        Assert.False(matcher.IsMatch(FileSystemPath.Parse("file.log")));
        Assert.False(matcher.IsMatch(FileSystemPath.Parse("file.tmp")));
    }

    [Theory]
    [InlineData("/test?.txt", "/test1.txt", true)]
    [InlineData("/test?.txt", "/testa.txt", true)]
    [InlineData("/test?.txt", "/test12.txt", false)]
    [InlineData("/file[0-9].txt", "/file5.txt", true)]
    [InlineData("/file[0-9].txt", "/filea.txt", false)]
    [InlineData("/file[abc].txt", "/filea.txt", true)]
    [InlineData("/file[abc].txt", "/filed.txt", false)]
    public void IsMatch_WithSpecialPatterns_HandlesCorrectly(string pattern, string path, bool expected)
    {
        var builder = new GlobMatcherBuilder();
        builder.AddInclude(Glob.Parse(pattern));
        var matcher = builder.Build();

        var result = matcher.IsMatch(FileSystemPath.Parse(path));

        Assert.Equal(expected, result);
    }

    //[Fact]
    //public void Match_WithPhysicalFileSystem_ReturnsMatchingFiles()
    //{
    //    var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    //    Directory.CreateDirectory(tempDir);

    //    try
    //    {
    //        File.WriteAllText(Path.Combine(tempDir, "file1.txt"), "content");
    //        File.WriteAllText(Path.Combine(tempDir, "file2.txt"), "content");
    //        File.WriteAllText(Path.Combine(tempDir, "file.log"), "content");
    //        Directory.CreateDirectory(Path.Combine(tempDir, "subfolder"));
    //        File.WriteAllText(Path.Combine(tempDir, "subfolder", "file3.txt"), "content");

    //        var fileSystem = new PhysicalFileSystem(new PhysicalFileSystemOptions
    //        {
    //            Root = tempDir
    //        });

    //        var builder = new GlobMatcherBuilder();
    //        builder.AddInclude(Glob.Parse("**/*.txt"));
    //        var matcher = builder.Build();

    //        var results = matcher.Match(fileSystem.RootDirectory);

    //        Assert.True(results.HasMatches);
    //        Assert.Equal(3, results.Files.Count());
    //    }
    //    finally
    //    {
    //        Directory.Delete(tempDir, true);
    //    }
    //}

    //[Fact]
    //public void Match_WithInMemoryFileSystem_ReturnsMatchingFiles()
    //{
    //    var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions
    //    {
    //        RootPath = "/test"
    //    });

    //    fileSystem.CreateFile("/test/file1.txt");
    //    fileSystem.CreateFile("/test/file2.txt");
    //    fileSystem.CreateFile("/test/file.log");
    //    fileSystem.CreateDirectory("/test/subfolder");
    //    fileSystem.CreateFile("/test/subfolder/file3.txt");

    //    var builder = new GlobMatcherBuilder();
    //    builder.AddInclude(Glob.Parse("**/*.txt"));
    //    var matcher = builder.Build();

    //    var results = matcher.Match(fileSystem.RootDirectory);

    //    Assert.True(results.HasMatches);
    //    Assert.Equal(3, results.Files.Count());
    //}

    //[Fact]
    //public void Match_WithExcludeDirectoriesOption_ExcludesDirectories()
    //{
    //    var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions
    //    {
    //        RootPath = "/test"
    //    });

    //    fileSystem.CreateDirectory("/test/folder1");
    //    fileSystem.CreateDirectory("/test/folder2");
    //    fileSystem.CreateFile("/test/file.txt");

    //    var options = new GlobMatcherOptions { ExcludeDirectories = true };
    //    var builder = new GlobMatcherBuilder(options);
    //    builder.AddInclude(Glob.Parse("**/*"));
    //    var matcher = builder.Build();

    //    var results = matcher.Match(fileSystem.RootDirectory);

    //    Assert.True(results.HasMatches);
    //    Assert.Single(results.Files);
    //    Assert.All(results.Files, item => Assert.IsAssignableFrom<IFileSystemFile>(item));
    //}

    //[Fact]
    //public void Match_WithNoMatches_ReturnsEmptyResult()
    //{
    //    var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions
    //    {
    //        RootPath = "/test"
    //    });

    //    fileSystem.CreateFile("/test/file.log");
    //    fileSystem.CreateFile("/test/file.tmp");

    //    var builder = new GlobMatcherBuilder();
    //    builder.AddInclude(Glob.Parse("*.txt"));
    //    var matcher = builder.Build();

    //    var results = matcher.Match(fileSystem.RootDirectory);

    //    Assert.False(results.HasMatches);
    //    Assert.Empty(results.Files);
    //}

    [Fact]
    public void IsMatch_WithNullFile_ThrowsArgumentNullException()
    {
        var builder = new GlobMatcherBuilder();
        builder.AddInclude(Glob.Parse("*.txt"));
        var matcher = builder.Build();

        Assert.Throws<ArgumentNullException>(() => matcher.IsMatch((IFileSystemFile)null!));
    }

    [Fact]
    public void IsMatch_WithNullDirectory_ThrowsArgumentNullException()
    {
        var builder = new GlobMatcherBuilder();
        builder.AddInclude(Glob.Parse("*.txt"));
        var matcher = builder.Build();

        Assert.Throws<ArgumentNullException>(() => matcher.IsMatch((IFileSystemDirectory)null!));
    }

    [Fact]
    public void Match_WithNullDirectory_ThrowsArgumentNullException()
    {
        var builder = new GlobMatcherBuilder();
        builder.AddInclude(Glob.Parse("*.txt"));
        var matcher = builder.Build();

        Assert.Throws<ArgumentNullException>(() => matcher.Match(null!));
    }
}
