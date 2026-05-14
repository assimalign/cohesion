using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Assimalign.Cohesion.FileSystem.Tests;

/// <summary>
/// Provider-agnostic contract tests for any <see cref="IFileSystem"/> implementation. Concrete
/// provider test projects inherit from this class and override <see cref="GetFileSystem"/> to
/// supply a fresh file system per test. Every test that mutates the file system MUST tolerate
/// running on a fresh empty file system.
/// </summary>
public abstract class FileSystemStandardTests
{
    /// <summary>
    /// Returns a fresh, empty file system for one test. Implementations should hand back a new
    /// instance per call so tests do not share state.
    /// </summary>
    public abstract IFileSystem GetFileSystem();

    // -----------------------------------------------------------------------
    // Create / Exists
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - Create + Exists: file round trip")]
    public void Create_File_ThenExists_True()
    {
        using var fs = GetFileSystem();

        var file = fs.CreateFile("hello.txt");

        Assert.NotNull(file);
        Assert.True(fs.Exists("hello.txt"));
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - Create + Exists: directory round trip")]
    public void Create_Directory_ThenExists_True()
    {
        using var fs = GetFileSystem();

        var dir = fs.CreateDirectory("etc");

        Assert.NotNull(dir);
        Assert.True(fs.Exists("etc"));
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - Create: nested path auto-creates intermediate directories")]
    public void Create_NestedFile_AutoCreatesParents()
    {
        using var fs = GetFileSystem();

        var file = fs.CreateFile("a/b/c/leaf.txt");

        Assert.NotNull(file);
        Assert.True(fs.Exists("a"));
        Assert.True(fs.Exists("a/b"));
        Assert.True(fs.Exists("a/b/c"));
        Assert.True(fs.Exists("a/b/c/leaf.txt"));
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - Create: duplicate file throws Conflict")]
    public void CreateFile_Duplicate_Conflict()
    {
        using var fs = GetFileSystem();
        fs.CreateFile("test.txt");

        var exception = Assert.Throws<FileSystemException>(() => fs.CreateFile("test.txt"));
        Assert.Equal(FileSystemErrorCode.Conflict, exception.Code);
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - Create: duplicate directory throws Conflict")]
    public void CreateDirectory_Duplicate_Conflict()
    {
        using var fs = GetFileSystem();
        fs.CreateDirectory("test");

        var exception = Assert.Throws<FileSystemException>(() => fs.CreateDirectory("test"));
        Assert.Equal(FileSystemErrorCode.Conflict, exception.Code);
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - Exists: returns false for non-existent path")]
    public void Exists_NonExistent_False()
    {
        using var fs = GetFileSystem();

        Assert.False(fs.Exists("nope"));
        Assert.False(fs.Exists("missing/path.txt"));
    }

    // -----------------------------------------------------------------------
    // Get*
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - GetFile: existing path returns file")]
    public void GetFile_Existing_ReturnsFile()
    {
        using var fs = GetFileSystem();
        fs.CreateFile("payload.bin");

        var file = fs.GetFile("payload.bin");

        Assert.NotNull(file);
        Assert.Equal("payload.bin", file.Name.ToString());
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - GetDirectory: existing path returns directory")]
    public void GetDirectory_Existing_ReturnsDirectory()
    {
        using var fs = GetFileSystem();
        fs.CreateDirectory("var/log");

        var dir = fs.GetDirectory("var/log");

        Assert.NotNull(dir);
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - GetFile: missing path throws NotFound")]
    public void GetFile_Missing_NotFound()
    {
        using var fs = GetFileSystem();

        var exception = Assert.Throws<FileSystemException>(() => fs.GetFile("missing.txt"));
        Assert.Equal(FileSystemErrorCode.NotFound, exception.Code);
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - GetDirectory: missing path throws NotFound")]
    public void GetDirectory_Missing_NotFound()
    {
        using var fs = GetFileSystem();

        var exception = Assert.Throws<FileSystemException>(() => fs.GetDirectory("nope"));
        Assert.Equal(FileSystemErrorCode.NotFound, exception.Code);
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - GetInfo: returns IFileSystemFile for file path")]
    public void GetInfo_FilePath_ReturnsFile()
    {
        using var fs = GetFileSystem();
        fs.CreateFile("info.txt");

        var info = fs.GetInfo("info.txt");

        Assert.IsAssignableFrom<IFileSystemFile>(info);
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - GetInfo: returns IFileSystemDirectory for directory path")]
    public void GetInfo_DirectoryPath_ReturnsDirectory()
    {
        using var fs = GetFileSystem();
        fs.CreateDirectory("dir");

        var info = fs.GetInfo("dir");

        Assert.IsAssignableFrom<IFileSystemDirectory>(info);
    }

    // -----------------------------------------------------------------------
    // File read / write
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - File: write + read round trip")]
    public void File_Write_Then_Read_RoundTrips()
    {
        using var fs = GetFileSystem();
        var file = fs.CreateFile("rt.bin");
        var content = Encoding.UTF8.GetBytes("round-trip");

        using (var stream = file.Open(FileMode.Open, FileAccess.Write))
        {
            stream.Write(content, 0, content.Length);
        }

        var readBack = new byte[content.Length];
        using (var stream = file.Open(FileMode.Open, FileAccess.Read))
        {
            stream.ReadExactly(readBack);
        }

        Assert.Equal(content, readBack);
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - File: size reflects writes")]
    public void File_Size_TracksWrites()
    {
        using var fs = GetFileSystem();
        var file = fs.CreateFile("sized.bin");

        Assert.Equal(0, file.Size.Length);

        var content = new byte[512];
        using (var stream = file.Open(FileMode.Open, FileAccess.Write))
        {
            stream.Write(content, 0, content.Length);
        }

        Assert.Equal(512, file.Size.Length);
    }

    // -----------------------------------------------------------------------
    // Delete
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - DeleteFile: removes existing file")]
    public void DeleteFile_Existing_Removes()
    {
        using var fs = GetFileSystem();
        fs.CreateFile("disposable.txt");
        Assert.True(fs.Exists("disposable.txt"));

        fs.DeleteFile("disposable.txt");

        Assert.False(fs.Exists("disposable.txt"));
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - DeleteFile: missing path throws NotFound")]
    public void DeleteFile_Missing_NotFound()
    {
        using var fs = GetFileSystem();

        var exception = Assert.Throws<FileSystemException>(() => fs.DeleteFile("missing.txt"));
        Assert.Equal(FileSystemErrorCode.NotFound, exception.Code);
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - DeleteDirectory: empty directory removes")]
    public void DeleteDirectory_Empty_Removes()
    {
        using var fs = GetFileSystem();
        fs.CreateDirectory("scratch");

        fs.DeleteDirectory("scratch");

        Assert.False(fs.Exists("scratch"));
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - DeleteDirectory: removes nested contents")]
    public void DeleteDirectory_WithContents_RemovesRecursively()
    {
        using var fs = GetFileSystem();
        fs.CreateDirectory("tree");
        fs.CreateFile("tree/leaf.txt");
        fs.CreateDirectory("tree/branch");
        fs.CreateFile("tree/branch/twig.txt");

        fs.DeleteDirectory("tree");

        Assert.False(fs.Exists("tree"));
        Assert.False(fs.Exists("tree/leaf.txt"));
        Assert.False(fs.Exists("tree/branch"));
        Assert.False(fs.Exists("tree/branch/twig.txt"));
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - DeleteDirectory: missing path throws NotFound")]
    public void DeleteDirectory_Missing_NotFound()
    {
        using var fs = GetFileSystem();

        var exception = Assert.Throws<FileSystemException>(() => fs.DeleteDirectory("missing"));
        Assert.Equal(FileSystemErrorCode.NotFound, exception.Code);
    }

    // -----------------------------------------------------------------------
    // Copy / Move
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - CopyFile: clones source content")]
    public void CopyFile_ClonesContent()
    {
        using var fs = GetFileSystem();
        var source = fs.CreateFile("src.bin");
        var content = Encoding.UTF8.GetBytes("source-bytes");

        using (var stream = source.Open(FileMode.Open, FileAccess.Write))
        {
            stream.Write(content, 0, content.Length);
        }

        fs.CopyFile("src.bin", "dst.bin");

        Assert.True(fs.Exists("src.bin"));
        Assert.True(fs.Exists("dst.bin"));

        var copy = fs.GetFile("dst.bin");
        var readBack = new byte[content.Length];
        using (var stream = copy.Open(FileMode.Open, FileAccess.Read))
        {
            stream.ReadExactly(readBack);
        }

        Assert.Equal(content, readBack);
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - CopyFile: source missing throws NotFound")]
    public void CopyFile_SourceMissing_NotFound()
    {
        using var fs = GetFileSystem();

        var exception = Assert.Throws<FileSystemException>(() => fs.CopyFile("missing.txt", "dst.txt"));
        Assert.Equal(FileSystemErrorCode.NotFound, exception.Code);
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - Move: relocates file")]
    public void Move_File_RelocatesPath()
    {
        using var fs = GetFileSystem();
        fs.CreateFile("origin.txt");

        fs.Move("origin.txt", "moved.txt");

        Assert.False(fs.Exists("origin.txt"));
        Assert.True(fs.Exists("moved.txt"));
    }

    // -----------------------------------------------------------------------
    // Enumeration
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - Enumerate: lists root entries")]
    public void Enumerate_Root_ListsEntries()
    {
        using var fs = GetFileSystem();
        fs.CreateDirectory("alpha");
        fs.CreateDirectory("beta");
        fs.CreateFile("gamma.txt");

        var entries = fs.EnumerateFileSystem().ToList();

        // The enumeration must surface every entry we just created. Compare by path segment so
        // the test is portable across providers that may root at different absolute prefixes.
        var leafNames = entries
            .Select(info => info.Path.GetSegments().LastOrDefault() ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("alpha", leafNames);
        Assert.Contains("beta", leafNames);
        Assert.Contains("gamma.txt", leafNames);
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - Directory.GetFiles: lists only file children")]
    public void Directory_GetFiles_OnlyFiles()
    {
        using var fs = GetFileSystem();
        fs.CreateDirectory("etc");
        fs.CreateFile("etc/hostname");
        fs.CreateFile("etc/hosts");
        fs.CreateDirectory("etc/sub");

        var etc = fs.GetDirectory("etc");
        var files = etc.GetFiles().ToList();

        Assert.Equal(2, files.Count);
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - Directory.GetDirectories: lists only directory children")]
    public void Directory_GetDirectories_OnlyDirs()
    {
        using var fs = GetFileSystem();
        fs.CreateDirectory("home");
        fs.CreateDirectory("home/alice");
        fs.CreateDirectory("home/bob");
        fs.CreateFile("home/readme.txt");

        var home = fs.GetDirectory("home");
        var dirs = home.GetDirectories().ToList();

        Assert.Equal(2, dirs.Count);
    }

    // -----------------------------------------------------------------------
    // Compatibility scenarios — Story L01.01.09.09.01.
    // The tests in this section guard against regressions in path depth,
    // payload size, and edge cases that the per-operation tests above don't
    // explicitly exercise. Every provider must satisfy them.
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - Deep nesting: 10 levels of auto-created parents")]
    public void DeepNesting_AutoCreatesEveryLevel()
    {
        using var fs = GetFileSystem();

        // 10 segments is comfortably under any realistic path-length limit while still being
        // deep enough to surface off-by-one bugs in segment iteration logic.
        const string deepPath = "lvl1/lvl2/lvl3/lvl4/lvl5/lvl6/lvl7/lvl8/lvl9/leaf.txt";
        var file = fs.CreateFile(deepPath);

        Assert.NotNull(file);
        Assert.True(fs.Exists(deepPath));
        // Spot-check several intermediate levels to make sure the entire chain materialized.
        Assert.True(fs.Exists("lvl1"));
        Assert.True(fs.Exists("lvl1/lvl2/lvl3/lvl4"));
        Assert.True(fs.Exists("lvl1/lvl2/lvl3/lvl4/lvl5/lvl6/lvl7/lvl8/lvl9"));
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - Large file: 1 MB round trip")]
    public void LargeFile_OneMegabyte_RoundTrips()
    {
        using var fs = GetFileSystem();
        var file = fs.CreateFile("large.bin");

        // 1 MiB filled with a recognizable pattern so a buffer truncation defect would corrupt
        // recognizable spots in the readback.
        var payload = new byte[1024 * 1024];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        using (var stream = file.Open(FileMode.Open, FileAccess.Write))
        {
            stream.Write(payload, 0, payload.Length);
        }

        Assert.Equal(payload.Length, file.Size.Length);

        var readBack = new byte[payload.Length];
        using (var stream = file.Open(FileMode.Open, FileAccess.Read))
        {
            stream.ReadExactly(readBack);
        }

        Assert.Equal(payload, readBack);
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - Empty file: zero-byte file round-trips")]
    public void EmptyFile_ZeroBytes_RoundTrips()
    {
        using var fs = GetFileSystem();
        var file = fs.CreateFile("empty.bin");

        Assert.Equal(0L, file.Size.Length);

        using (var stream = file.Open(FileMode.Open, FileAccess.Read))
        {
            // Reading from an empty stream must return 0 without throwing.
            var buffer = new byte[16];
            int read = stream.Read(buffer, 0, buffer.Length);
            Assert.Equal(0, read);
        }

        Assert.True(fs.Exists("empty.bin"));
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - CopyFile: destination already exists throws Conflict")]
    public void CopyFile_DestinationExists_Conflict()
    {
        using var fs = GetFileSystem();
        fs.CreateFile("src.bin");
        fs.CreateFile("dst.bin");

        var exception = Assert.Throws<FileSystemException>(() => fs.CopyFile("src.bin", "dst.bin"));
        Assert.Equal(FileSystemErrorCode.Conflict, exception.Code);
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - Move: source missing throws NotFound")]
    public void Move_SourceMissing_NotFound()
    {
        using var fs = GetFileSystem();

        var exception = Assert.Throws<FileSystemException>(() => fs.Move("missing.txt", "moved.txt"));
        Assert.Equal(FileSystemErrorCode.NotFound, exception.Code);
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - Directory: empty directory enumerates to no children")]
    public void EmptyDirectory_HasNoChildren()
    {
        using var fs = GetFileSystem();
        var dir = fs.CreateDirectory("empty");

        Assert.Empty(dir.GetFiles());
        Assert.Empty(dir.GetDirectories());
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - GetFile: aggregate path is preserved through GetFile + Directory")]
    public void GetFile_PathPreserved()
    {
        using var fs = GetFileSystem();
        fs.CreateFile("docs/readme.txt");

        var file = fs.GetFile("docs/readme.txt");

        // The exact string representation may vary across providers (e.g. leading slash), but
        // the leaf name must always be addressable through the returned IFileSystemFile.
        var segments = file.Path.GetSegments();
        Assert.Equal("readme.txt", segments[^1]);
        Assert.Equal("docs", segments[^2]);
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - File: re-open after dispose returns fresh stream")]
    public void File_ReopenAfterDispose_FreshStream()
    {
        using var fs = GetFileSystem();
        var file = fs.CreateFile("rotated.log");
        var payload = Encoding.UTF8.GetBytes("first");

        // Write, dispose the stream, then re-open and read. The reopen must surface the bytes
        // we just wrote — a stream cached internally by the provider would otherwise stale out.
        using (var stream = file.Open(FileMode.Open, FileAccess.Write))
        {
            stream.Write(payload, 0, payload.Length);
        }

        var readBack = new byte[payload.Length];
        using (var stream = file.Open(FileMode.Open, FileAccess.Read))
        {
            stream.ReadExactly(readBack);
        }

        Assert.Equal(payload, readBack);
    }

    [Fact(DisplayName = "Cohesion Contract [FileSystem] - File: CreatedOn populated after Create")]
    public void File_CreatedOn_Populated()
    {
        using var fs = GetFileSystem();
        var before = DateTime.UtcNow.AddSeconds(-5);
        var file = fs.CreateFile("timestamped.txt");
        var after = DateTime.UtcNow.AddSeconds(5);

        // CreatedOn is supplied by the provider — it may be UTC or local time. Normalize to UTC
        // for the comparison; either kind is acceptable as long as the value sits in the window.
        var createdUtc = file.CreatedOn.Kind == DateTimeKind.Utc
            ? file.CreatedOn
            : file.CreatedOn.ToUniversalTime();

        Assert.True(createdUtc >= before, $"CreatedOn={createdUtc:o} is before window start {before:o}");
        Assert.True(createdUtc <= after, $"CreatedOn={createdUtc:o} is after window end {after:o}");
    }
}
