using System;
using System.IO;
using System.Linq;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.FileSystem.Tests;

/// <summary>
/// Proves an <see cref="InMemoryFileSystem"/> resolves every path-taking operation inside its root
/// (#1180). Nothing exists outside an in-memory tree to leak, but the provider used to alias a
/// sibling-prefix path such as <c>/datax</c> to the in-root entry <c>/data/x</c> and to resolve
/// parent segments to unrelated entries, so it gets the same containment rule as the physical
/// provider: resolve the full path, then require the root or a path under it.
/// </summary>
public sealed class InMemoryFileSystemContainmentTests : IDisposable
{
    private static readonly string[] _escapes =
    [
        "..",
        "../index.html",
        "/../index.html",
        "/datax",
        "/data2/index.html",
        "/other/index.html",
    ];

    private readonly InMemoryFileSystem _fileSystem;

    public InMemoryFileSystemContainmentTests()
    {
        _fileSystem = CreateFileSystem(ignoreCase: true);
    }

    /// <summary>
    /// Every path-taking member of <see cref="IFileSystem"/>; copy and move are exercised from both ends.
    /// </summary>
    public enum Operation
    {
        Exists,
        GetInfo,
        GetFile,
        GetDirectory,
        CreateFile,
        CreateDirectory,
        DeleteFile,
        DeleteDirectory,
        CopyFileSource,
        CopyFileDestination,
        MoveSource,
        MoveDestination,
    }

    public static TheoryData<Operation, string> EscapeCases()
    {
        var data = new TheoryData<Operation, string>();
        foreach (Operation operation in Enum.GetValues<Operation>())
        {
            foreach (string escape in _escapes)
            {
                data.Add(operation, escape);
            }
        }

        return data;
    }

    public void Dispose()
    {
        _fileSystem.Dispose();
    }

    [Theory(DisplayName = "Cohesion Test [InMemoryFileSystem] - Containment: every path-taking operation should refuse a path outside the root")]
    [MemberData(nameof(EscapeCases))]
    public void Operation_PathOutsideRoot_ShouldThrowPathOutsideRoot(Operation operation, string path)
    {
        // Arrange
        FileSystemPath escape = FileSystemPath.Parse(path);

        // Act
        var exception = Should.Throw<FileSystemException>(() => Invoke(_fileSystem, operation, escape));

        // Assert
        exception.Code.ShouldBe(FileSystemErrorCode.PathOutsideRoot);
        AssertTreeUnchanged(_fileSystem);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Containment: a sibling-prefix path should not alias an in-root entry")]
    public void Exists_SiblingPrefixOfInRootEntry_ShouldThrowPathOutsideRoot()
    {
        // Arrange — "/datax" shares its text with the in-root entry "/data/x" once the root's
        // length is cut off, which is how the provider used to report it as existing.
        FileSystemPath sibling = FileSystemPath.Parse("/datax");

        // Act
        var exception = Should.Throw<FileSystemException>(() => _fileSystem.Exists(sibling));

        // Assert
        exception.Code.ShouldBe(FileSystemErrorCode.PathOutsideRoot);
        _fileSystem.Exists("x").ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [InMemoryFileSystem] - Containment: a path that stays inside the root should resolve")]
    [InlineData("index.html", "/data/index.html")]
    [InlineData("./index.html", "/data/index.html")]
    [InlineData("/data/index.html", "/data/index.html")]
    [InlineData("../data/index.html", "/data/index.html")]
    [InlineData("/DATA/index.html", "/data/index.html")]
    [InlineData("nested", "/data/nested")]
    [InlineData("nested/./page.html", "/data/nested/page.html")]
    [InlineData("/data/nested/", "/data/nested")]
    public void GetInfo_PathInsideRoot_ShouldResolveUnderRoot(string path, string expected)
    {
        // Arrange
        FileSystemPath inside = FileSystemPath.Parse(path);

        // Act
        IFileSystemInfo info = _fileSystem.GetInfo(inside);

        // Assert
        info.Path.ToString().ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [InMemoryFileSystem] - Containment: the root itself should resolve to the root directory")]
    [InlineData("")]
    [InlineData("/data")]
    [InlineData("../data")]
    public void GetInfo_RootItself_ShouldReturnRootDirectory(string path)
    {
        // Arrange
        FileSystemPath root = FileSystemPath.Parse(path);

        // Act
        IFileSystemInfo info = _fileSystem.GetInfo(root);

        // Assert
        info.ShouldBeSameAs(_fileSystem.RootDirectory);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Containment: a case-sensitive file system should refuse a differently cased root")]
    public void GetInfo_DifferentlyCasedRootWhenCaseSensitive_ShouldThrowPathOutsideRoot()
    {
        // Arrange
        using InMemoryFileSystem caseSensitive = CreateFileSystem(ignoreCase: false);

        // Act
        var exception = Should.Throw<FileSystemException>(() => caseSensitive.GetInfo("/DATA/index.html"));

        // Assert
        exception.Code.ShouldBe(FileSystemErrorCode.PathOutsideRoot);
        caseSensitive.GetInfo("/data/index.html").ShouldBeAssignableTo<IFileSystemFile>();
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Containment: deleting an entry under a nested root should resolve its parent")]
    public void DeleteFile_UnderNestedRoot_ShouldRemoveEntry()
    {
        // Arrange — resolving the parent of a root-level entry once sliced past the end of the
        // root path when the root was anything other than "/".
        _fileSystem.CreateFile("removable.txt");

        // Act
        _fileSystem.DeleteFile("removable.txt");

        // Assert
        _fileSystem.Exists("removable.txt").ShouldBeFalse();
        AssertTreeUnchanged(_fileSystem);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Containment: parent segments at the namespace root should stay at the root")]
    public void GetInfo_ParentSegmentsAtNamespaceRoot_ShouldResolveUnderRoot()
    {
        // Arrange — with the root at "/" there is nothing above it: "/.." is "/" on every host, so
        // a leading ".." resolves to the root rather than to an unrelated entry.
        using var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions { RootPath = "/" });
        fileSystem.CreateFile("index.html");

        // Act
        IFileSystemInfo info = fileSystem.GetInfo("../index.html");

        // Assert
        info.Path.ToString().ShouldBe("/index.html");
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Containment: a name that only begins with two dots should be an ordinary entry")]
    public void CreateFile_NameStartingWithTwoDots_ShouldCreateRootLevelEntry()
    {
        // Arrange
        using var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions { RootPath = "/" });

        // Act
        IFileSystemFile file = fileSystem.CreateFile("..config");

        // Assert
        file.Path.ToString().ShouldBe("/..config");
        fileSystem.Exists("..config").ShouldBeTrue();
        fileSystem.EnumerateFileSystem().Single().ShouldBeAssignableTo<IFileSystemFile>();
    }

    private static InMemoryFileSystem CreateFileSystem(bool ignoreCase)
    {
        var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions
        {
            RootPath = "/data",
            IgnoreCase = ignoreCase,
        });
        fileSystem.CreateFile("index.html");
        fileSystem.CreateFile("x");
        fileSystem.CreateDirectory("nested");
        fileSystem.CreateFile("nested/page.html");
        return fileSystem;
    }

    private static void Invoke(InMemoryFileSystem fileSystem, Operation operation, FileSystemPath path)
    {
        switch (operation)
        {
            case Operation.Exists:
                fileSystem.Exists(path);
                break;
            case Operation.GetInfo:
                fileSystem.GetInfo(path);
                break;
            case Operation.GetFile:
                fileSystem.GetFile(path);
                break;
            case Operation.GetDirectory:
                fileSystem.GetDirectory(path);
                break;
            case Operation.CreateFile:
                fileSystem.CreateFile(path);
                break;
            case Operation.CreateDirectory:
                fileSystem.CreateDirectory(path);
                break;
            case Operation.DeleteFile:
                fileSystem.DeleteFile(path);
                break;
            case Operation.DeleteDirectory:
                fileSystem.DeleteDirectory(path);
                break;
            case Operation.CopyFileSource:
                fileSystem.CopyFile(path, "copied.txt");
                break;
            case Operation.CopyFileDestination:
                fileSystem.CopyFile("index.html", path);
                break;
            case Operation.MoveSource:
                fileSystem.Move(path, "moved.txt");
                break;
            case Operation.MoveDestination:
                fileSystem.Move("index.html", path);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    private static void AssertTreeUnchanged(InMemoryFileSystem fileSystem)
    {
        fileSystem.Exists("index.html").ShouldBeTrue();
        fileSystem.Exists("x").ShouldBeTrue();
        fileSystem.Exists("nested/page.html").ShouldBeTrue();
        fileSystem.EnumerateFileSystem().Count().ShouldBe(3);
        fileSystem.GetDirectory("nested").EnumerateFileSystem().Count().ShouldBe(1);
    }
}
