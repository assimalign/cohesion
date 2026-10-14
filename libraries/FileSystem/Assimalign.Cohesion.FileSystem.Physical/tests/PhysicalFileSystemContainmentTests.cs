using System;
using System.Diagnostics;
using System.IO;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.FileSystem.Physical.Tests;

/// <summary>
/// Proves a <see cref="PhysicalFileSystem"/> keeps every path-taking operation inside its root
/// (#1180). The root is <c>base/public</c>; beside it sit <c>base/secret.txt</c> and a sibling
/// <c>base/public2/</c> whose name begins with the root's own text. Every escape below tries, and
/// fails, to reach them, and the sandbox above <c>base</c> catches anything that climbs further.
/// </summary>
public sealed class PhysicalFileSystemContainmentTests : IDisposable
{
    private const string secretContent = "top secret";
    private const string siblingContent = "sibling secret";
    private const string indexContent = "public index";

    private static readonly string[] _escapes =
    [
        "..",
        "../secret.txt",
        "..\\secret.txt",
        "../public2/sibling.txt",
        "../../secret.txt",
        "{base}/secret.txt",
        "{base}/public2",
        "{base}/public2/sibling.txt",
    ];

    private readonly string _sandbox;
    private readonly string _base;
    private readonly string _rootPath;
    private readonly PhysicalFileSystem _fileSystem;

    public PhysicalFileSystemContainmentTests()
    {
        _sandbox = Directory.CreateTempSubdirectory("cohesion-containment-").FullName;
        _base = Directory.CreateDirectory(Path.Combine(_sandbox, "base")).FullName;
        _rootPath = Directory.CreateDirectory(Path.Combine(_base, "public")).FullName;
        Directory.CreateDirectory(Path.Combine(_rootPath, "nested"));
        Directory.CreateDirectory(Path.Combine(_base, "public2"));
        File.WriteAllText(Path.Combine(_base, "secret.txt"), secretContent);
        File.WriteAllText(Path.Combine(_base, "public2", "sibling.txt"), siblingContent);
        File.WriteAllText(Path.Combine(_rootPath, "index.html"), indexContent);
        File.WriteAllText(Path.Combine(_rootPath, "nested", "page.html"), "nested page");

        _fileSystem = new PhysicalFileSystem(new PhysicalFileSystemOptions
        {
            Root = FileSystemPath.Parse(_rootPath),
        });
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
        try
        {
            Directory.Delete(_sandbox, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a per-test temporary directory; it must not fail the test.
        }
    }

    [Theory(DisplayName = "Cohesion Test [PhysicalFileSystem] - Containment: every path-taking operation should refuse a path outside the root")]
    [MemberData(nameof(EscapeCases))]
    public void Operation_PathOutsideRoot_ShouldThrowPathOutsideRoot(Operation operation, string template)
    {
        // Arrange
        FileSystemPath path = Expand(template);

        // Act
        var exception = Should.Throw<FileSystemException>(() => Invoke(operation, path));

        // Assert
        exception.Code.ShouldBe(FileSystemErrorCode.PathOutsideRoot);
        AssertOutsideUntouched();
        File.ReadAllText(Path.Combine(_rootPath, "index.html")).ShouldBe(indexContent);
    }

    [Theory(DisplayName = "Cohesion Test [PhysicalFileSystem] - Containment: a rooted path naming another location should be refused")]
    [InlineData("/index.html")]
    [InlineData("/../index.html")]
    [InlineData("//server/share/index.html")]
    [InlineData("C:index.html")]
    public void GetInfo_RootedPathElsewhere_ShouldThrowPathOutsideRoot(string path)
    {
        // Arrange — on Windows "/index.html" and "C:index.html" resolve against the current drive or
        // directory, and a UNC path names another host; on Unix the first three are host-absolute and
        // "C:index.html" is rooted in the FileSystemPath model. None is the root or under it.
        FileSystemPath rooted = FileSystemPath.Parse(path);

        // Act
        var exception = Should.Throw<FileSystemException>(() => _fileSystem.GetInfo(rooted));

        // Assert
        exception.Code.ShouldBe(FileSystemErrorCode.PathOutsideRoot);
        AssertOutsideUntouched();
    }

    [Theory(DisplayName = "Cohesion Test [PhysicalFileSystem] - Containment: a path that stays inside the root should resolve")]
    [InlineData("index.html", "index.html")]
    [InlineData("./index.html", "index.html")]
    [InlineData("nested/page.html", "nested/page.html")]
    [InlineData("nested\\page.html", "nested/page.html")]
    [InlineData("../public/index.html", "index.html")]
    [InlineData("../public/nested/page.html", "nested/page.html")]
    [InlineData("{root}/index.html", "index.html")]
    [InlineData("{root}/nested/page.html", "nested/page.html")]
    [InlineData("{root}/nested/", "nested")]
    public void GetInfo_PathInsideRoot_ShouldResolveUnderRoot(string template, string expectedRelative)
    {
        // Arrange
        FileSystemPath path = Expand(template);
        FileSystemPath expected = FileSystemPath.Parse(Path.Combine(_rootPath, expectedRelative));

        // Act
        IFileSystemInfo info = _fileSystem.GetInfo(path);

        // Assert
        info.Path.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [PhysicalFileSystem] - Containment: the root itself should resolve to the root directory")]
    [InlineData("")]
    [InlineData("{root}")]
    [InlineData("{root}/")]
    [InlineData("../public")]
    public void GetInfo_RootItself_ShouldReturnRootDirectory(string template)
    {
        // Arrange
        FileSystemPath path = Expand(template);

        // Act
        IFileSystemInfo info = _fileSystem.GetInfo(path);

        // Assert
        info.ShouldBeAssignableTo<IFileSystemDirectory>();
        info.Path.ShouldBe(_fileSystem.RootDirectory.Path);
    }

    [Theory(DisplayName = "Cohesion Test [PhysicalFileSystem] - Containment: encoded traversal should stay a literal name inside the root")]
    [InlineData("%2e%2e/secret.txt")]
    [InlineData("%2E%2E/secret.txt")]
    [InlineData("..%2fsecret.txt")]
    [InlineData("..%5csecret.txt")]
    [InlineData("%2e%2e%2fsecret.txt")]
    [InlineData("..%c0%afsecret.txt")]
    [InlineData("\uFF0E\uFF0E/secret.txt")]
    public void GetFile_EncodedTraversal_ShouldNotDecode(string path)
    {
        // Arrange — the provider takes paths, not URLs: percent-encoding and look-alike full stops
        // are literal characters of an in-root name, never a parent segment.
        FileSystemPath literal = FileSystemPath.Parse(path);

        // Act
        bool exists = _fileSystem.Exists(literal);
        var exception = Should.Throw<FileSystemException>(() => _fileSystem.GetFile(literal));

        // Assert
        exists.ShouldBeFalse();
        exception.Code.ShouldBe(FileSystemErrorCode.NotFound);
        AssertOutsideUntouched();
    }

    [Fact(DisplayName = "Cohesion Test [PhysicalFileSystem] - Containment: a differently cased root should follow the platform's case sensitivity")]
    public void GetInfo_DifferentlyCasedRoot_ShouldFollowPlatformCaseSensitivity()
    {
        // Arrange
        FileSystemPath path = FileSystemPath.Parse(_rootPath.ToUpperInvariant() + "/index.html");
        bool caseInsensitive = OperatingSystem.IsWindows()
            || OperatingSystem.IsMacOS()
            || OperatingSystem.IsIOS()
            || OperatingSystem.IsTvOS()
            || OperatingSystem.IsWatchOS();

        // Act
        Func<IFileSystemInfo> getInfo = () => _fileSystem.GetInfo(path);

        // Assert
        if (caseInsensitive)
        {
            // The provider rebuilds the location from its own root text, so a case-insensitive
            // match can never hand the disk a differently cased root on a case-sensitive volume.
            getInfo().Path.ShouldBe(FileSystemPath.Parse(Path.Combine(_rootPath, "index.html")));
        }
        else
        {
            Should.Throw<FileSystemException>(() => getInfo()).Code.ShouldBe(FileSystemErrorCode.PathOutsideRoot);
        }
    }

    [Fact(DisplayName = "Cohesion Test [PhysicalFileSystem] - Containment: a device name the operating system maps outside the root should be refused")]
    public void GetInfo_ReservedDeviceName_ShouldFollowOperatingSystemNormalization()
    {
        // Arrange — Windows maps a final "NUL" segment to the \\.\NUL device whatever directory
        // precedes it; elsewhere "NUL" is an ordinary name. The provider checks the location the
        // operating system will actually open, so it follows whichever the platform does.
        string normalized = Path.GetFullPath(Path.Join(_rootPath, "NUL"));
        bool mapsOutside = !normalized.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase);

        // Act
        var exception = Should.Throw<FileSystemException>(() => _fileSystem.GetInfo("NUL"));

        // Assert
        exception.Code.ShouldBe(mapsOutside ? FileSystemErrorCode.PathOutsideRoot : FileSystemErrorCode.NotFound);
    }

    [Fact(DisplayName = "Cohesion Test [PhysicalFileSystem] - Containment: the root directory should have no parent")]
    public void RootDirectory_Parent_ShouldBeNull()
    {
        // Arrange
        IFileSystemDirectory nested = _fileSystem.GetDirectory("nested");
        IFileSystemFile index = _fileSystem.GetFile("index.html");

        // Act
        IFileSystemDirectory? rootParent = _fileSystem.RootDirectory.Parent;
        IFileSystemDirectory? nestedParent = nested.Parent;
        IFileSystemDirectory? indexDirectoryParent = index.Directory.Parent;

        // Assert
        rootParent.ShouldBeNull();
        indexDirectoryParent.ShouldBeNull();
        nestedParent.ShouldNotBeNull();
        nestedParent.Path.ShouldBe(_fileSystem.RootDirectory.Path);
    }

    [Fact(DisplayName = "Cohesion Test [PhysicalFileSystem] - Containment: parent navigation from a subdirectory should work while it stays inside the root")]
    public void DirectoryExists_ParentNavigationInsideRoot_ShouldResolve()
    {
        // Arrange
        IFileSystemDirectory nested = _fileSystem.GetDirectory("nested");

        // Act
        bool sibling = nested.Exists("../index.html");
        var escape = Should.Throw<FileSystemException>(() => nested.Exists("../../secret.txt"));

        // Assert
        sibling.ShouldBeTrue();
        escape.Code.ShouldBe(FileSystemErrorCode.PathOutsideRoot);
    }

    [Fact(DisplayName = "Cohesion Test [PhysicalFileSystem] - Containment: a link inside the root should be followed (lexical containment)")]
    public void GetFile_ThroughLinkInsideRoot_ShouldFollowLink()
    {
        // Arrange — the documented policy: containment is decided on the normalized path text, the
        // same text handed to the operating system, which then follows any link on it. A link inside
        // the root that points outside it is therefore followed; whoever can create one already
        // controls the root's content. Where the platform cannot create a link the policy has
        // nothing to show, so the test ends there.
        string link = Path.Combine(_rootPath, "linked");
        if (!TryCreateDirectoryLink(link, Path.Combine(_base, "public2")))
        {
            return;
        }

        try
        {
            // Act
            IFileSystemFile file = _fileSystem.GetFile("linked/sibling.txt");
            string content;
            using (var reader = new StreamReader(file.Open(FileMode.Open, FileAccess.Read, FileShare.Read)))
            {
                content = reader.ReadToEnd();
            }

            // Assert
            content.ShouldBe(siblingContent);
            file.Path.ShouldBe(FileSystemPath.Parse(Path.Combine(link, "sibling.txt")));
            Should.Throw<FileSystemException>(() => _fileSystem.GetFile("../public2/sibling.txt"))
                .Code.ShouldBe(FileSystemErrorCode.PathOutsideRoot);
        }
        finally
        {
            // Remove the link itself, never its target. A recursive delete of the sandbox reports
            // access denied for a junction on Windows even though it removes it.
            try
            {
                Directory.Delete(link);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort; Dispose removes the sandbox either way.
            }
        }
    }

    private FileSystemPath Expand(string template)
    {
        return FileSystemPath.Parse(template
            .Replace("{root}", _rootPath.Replace('\\', '/'), StringComparison.Ordinal)
            .Replace("{base}", _base.Replace('\\', '/'), StringComparison.Ordinal));
    }

    private void Invoke(Operation operation, FileSystemPath path)
    {
        switch (operation)
        {
            case Operation.Exists:
                _fileSystem.Exists(path);
                break;
            case Operation.GetInfo:
                _fileSystem.GetInfo(path);
                break;
            case Operation.GetFile:
                _fileSystem.GetFile(path);
                break;
            case Operation.GetDirectory:
                _fileSystem.GetDirectory(path);
                break;
            case Operation.CreateFile:
                _fileSystem.CreateFile(path);
                break;
            case Operation.CreateDirectory:
                _fileSystem.CreateDirectory(path);
                break;
            case Operation.DeleteFile:
                _fileSystem.DeleteFile(path);
                break;
            case Operation.DeleteDirectory:
                _fileSystem.DeleteDirectory(path);
                break;
            case Operation.CopyFileSource:
                _fileSystem.CopyFile(path, "copied.txt");
                break;
            case Operation.CopyFileDestination:
                _fileSystem.CopyFile("index.html", path);
                break;
            case Operation.MoveSource:
                _fileSystem.Move(path, "moved.txt");
                break;
            case Operation.MoveDestination:
                _fileSystem.Move("index.html", path);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    private void AssertOutsideUntouched()
    {
        File.ReadAllText(Path.Combine(_base, "secret.txt")).ShouldBe(secretContent);
        File.ReadAllText(Path.Combine(_base, "public2", "sibling.txt")).ShouldBe(siblingContent);
        Directory.GetFileSystemEntries(_sandbox).Length.ShouldBe(1);
        Directory.GetFileSystemEntries(_base).Length.ShouldBe(3);
        Directory.GetFileSystemEntries(Path.Combine(_base, "public2")).Length.ShouldBe(1);
        File.Exists(Path.Combine(_rootPath, "copied.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(_rootPath, "moved.txt")).ShouldBeFalse();
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // Windows refuses symbolic links without the create-symbolic-link privilege (no
            // Developer Mode, not elevated). A directory junction needs no privilege and the
            // operating system follows it the same way, so fall through to one there.
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(target);

        using Process? process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}
