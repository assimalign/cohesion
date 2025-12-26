using System;
using System.IO;
using System.Linq;

namespace Assimalign.Cohesion.FileSystem.Globbing.Tests;

public class IntegrationTests
{
    [Fact]
    public void GlobMatcher_WithComplexPatterns_WorksEndToEnd()
    {
        var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions
        {
            RootPath = "/project"
        });

        // Create a typical project structure
        fileSystem.CreateFile("/project/Program.cs");
        fileSystem.CreateFile("/project/Readme.md");
        fileSystem.CreateDirectory("/project/src");
        fileSystem.CreateFile("/project/src/Models/User.cs");
        fileSystem.CreateFile("/project/src/Services/UserService.cs");
        fileSystem.CreateDirectory("/project/tests");
        fileSystem.CreateFile("/project/tests/UserTests.cs");
        fileSystem.CreateFile("/project/bin/Debug/app.dll");
        fileSystem.CreateFile("/project/obj/project.assets.json");

        // Match all C# files except in bin/obj directories
        var builder = new GlobMatcherBuilder();
        builder.AddInclude(Glob.Parse("**/*.cs"))
               .AddExclude(Glob.Parse("**/bin/**"))
               .AddExclude(Glob.Parse("**/obj/**"));

        var matcher = builder.Build();
        var results = matcher.Match(fileSystem.RootDirectory);

        Assert.True(results.HasMatches);
        Assert.Equal(4, results.Files.Count());
        Assert.All(results.Files, file => Assert.EndsWith(".cs", file.Path));
        Assert.DoesNotContain(results.Files, file => file.Path.ToString().Contains("/bin/"));
        Assert.DoesNotContain(results.Files, file => file.Path.ToString().Contains("/obj/"));
    }

    [Fact]
    public void GlobMatcher_WithWildcardDirectory_MatchesNestedFiles()
    {
        var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions
        {
            RootPath = "/root"
        });

        fileSystem.CreateFile("/root/level1/file.txt");
        fileSystem.CreateFile("/root/level1/level2/file.txt");
        fileSystem.CreateFile("/root/level1/level2/level3/file.txt");

        var builder = new GlobMatcherBuilder();
        builder.AddInclude(Glob.Parse("**/file.txt"));
        var matcher = builder.Build();

        var results = matcher.Match(fileSystem.RootDirectory);

        Assert.True(results.HasMatches);
        Assert.Equal(3, results.Files.Count());
    }

    [Fact]
    public void GlobMatcher_WithMultipleExtensions_MatchesCorrectly()
    {
        var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions
        {
            RootPath = "/docs"
        });

        fileSystem.CreateFile("/docs/readme.md");
        fileSystem.CreateFile("/docs/guide.md");
        fileSystem.CreateFile("/docs/api.txt");
        fileSystem.CreateFile("/docs/changelog.log");

        var builder = new GlobMatcherBuilder();
        builder.AddInclude(Glob.Parse("*.md"))
               .AddInclude(Glob.Parse("*.txt"));
        var matcher = builder.Build();

        var results = matcher.Match(fileSystem.RootDirectory);

        Assert.True(results.HasMatches);
        Assert.Equal(3, results.Files.Count());
        Assert.DoesNotContain(results.Files, file => file.Path.ToString().EndsWith(".log"));
    }

    [Fact]
    public void GlobMatcher_WithPhysicalFileSystem_WorksCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create test structure
            Directory.CreateDirectory(Path.Combine(tempDir, "src"));
            Directory.CreateDirectory(Path.Combine(tempDir, "tests"));
            File.WriteAllText(Path.Combine(tempDir, "src", "App.cs"), "// code");
            File.WriteAllText(Path.Combine(tempDir, "tests", "AppTests.cs"), "// tests");
            File.WriteAllText(Path.Combine(tempDir, "Readme.md"), "# Readme");

            var fileSystem = new PhysicalFileSystem(new PhysicalFileSystemOptions
            {
                Root = tempDir
            });

            var builder = new GlobMatcherBuilder();
            builder.AddInclude(Glob.Parse("**/*.cs"));
            var matcher = builder.Build();

            var results = matcher.Match(fileSystem.RootDirectory);

            Assert.True(results.HasMatches);
            Assert.Equal(2, results.Files.Count());
            Assert.All(results.Files, file => Assert.EndsWith(".cs", file.Path));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GlobMatcher_WithCharacterSets_MatchesCorrectly()
    {
        var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions
        {
            RootPath = "/files"
        });

        fileSystem.CreateFile("/files/file1.txt");
        fileSystem.CreateFile("/files/file2.txt");
        fileSystem.CreateFile("/files/file3.txt");
        fileSystem.CreateFile("/files/filea.txt");

        var builder = new GlobMatcherBuilder();
        builder.AddInclude(Glob.Parse("file[0-9].txt"));
        var matcher = builder.Build();

        var results = matcher.Match(fileSystem.RootDirectory);

        Assert.True(results.HasMatches);
        Assert.Equal(3, results.Files.Count());
        Assert.All(results.Files, file =>
        {
            var name = file.Path.ToString();
            Assert.Matches(@"file[0-9]\.txt", name);
        });
    }

    [Fact]
    public void GlobMatcher_WithSingleCharacterWildcard_MatchesCorrectly()
    {
        var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions
        {
            RootPath = "/files"
        });

        fileSystem.CreateFile("/files/test1.txt");
        fileSystem.CreateFile("/files/test2.txt");
        fileSystem.CreateFile("/files/testa.txt");
        fileSystem.CreateFile("/files/test12.txt");

        var builder = new GlobMatcherBuilder();
        builder.AddInclude(Glob.Parse("test?.txt"));
        var matcher = builder.Build();

        var results = matcher.Match(fileSystem.RootDirectory);

        Assert.True(results.HasMatches);
        Assert.Equal(3, results.Files.Count());
        Assert.DoesNotContain(results.Files, file => file.Path.ToString().EndsWith("test12.txt"));
    }

    [Fact]
    public void GlobMatcher_WithExcludeDirectories_FiltersDirectories()
    {
        var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions
        {
            RootPath = "/workspace"
        });

        fileSystem.CreateDirectory("/workspace/folder1");
        fileSystem.CreateDirectory("/workspace/folder2");
        fileSystem.CreateDirectory("/workspace/folder1/subfolder");
        fileSystem.CreateFile("/workspace/file.txt");
        fileSystem.CreateFile("/workspace/folder1/file.txt");

        var options = new GlobMatcherOptions { ExcludeDirectories = true };
        var builder = new GlobMatcherBuilder(options);
        builder.AddInclude(Glob.Parse("**/*"));
        var matcher = builder.Build();

        var results = matcher.Match(fileSystem.RootDirectory);

        Assert.True(results.HasMatches);
        Assert.All(results.Files, item => Assert.IsAssignableFrom<IFileSystemFile>(item));
        Assert.DoesNotContain(results.Files, item => item is IFileSystemDirectory);
    }

    [Fact]
    public void GlobMatcher_WithEmptyDirectory_ReturnsEmpty()
    {
        var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions
        {
            RootPath = "/empty"
        });

        var builder = new GlobMatcherBuilder();
        builder.AddInclude(Glob.Parse("**/*"));
        var matcher = builder.Build();

        var results = matcher.Match(fileSystem.RootDirectory);

        Assert.False(results.HasMatches);
        Assert.Empty(results.Files);
    }

    [Fact]
    public void GlobMatcher_WithCaseSensitivity_RespectsOption()
    {
        var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions
        {
            RootPath = "/case"
        });

        fileSystem.CreateFile("/case/File.TXT");
        fileSystem.CreateFile("/case/file.txt");

        // Case insensitive
        var ignoreCase = new GlobMatcherOptions { IgnoreCase = true };
        var builderIgnoreCase = new GlobMatcherBuilder(ignoreCase);
        builderIgnoreCase.AddInclude(Glob.Parse("*.txt"));
        var matcherIgnoreCase = builderIgnoreCase.Build();

        var resultsIgnoreCase = matcherIgnoreCase.Match(fileSystem.RootDirectory);

        Assert.True(resultsIgnoreCase.HasMatches);
        Assert.Equal(2, resultsIgnoreCase.Files.Count());

        // Case sensitive
        var caseSensitive = new GlobMatcherOptions { IgnoreCase = false };
        var builderCaseSensitive = new GlobMatcherBuilder(caseSensitive);
        builderCaseSensitive.AddInclude(Glob.Parse("*.txt"));
        var matcherCaseSensitive = builderCaseSensitive.Build();

        var resultsCaseSensitive = matcherCaseSensitive.Match(fileSystem.RootDirectory);

        Assert.True(resultsCaseSensitive.HasMatches);
        Assert.Single(resultsCaseSensitive.Files);
    }
}
