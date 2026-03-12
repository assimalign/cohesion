using System;
using System.IO;
using System.Linq;
using System.Text;

using Xunit;

namespace Assimalign.Cohesion.FileSystem.Tests;

/// <summary>
/// Standard tests inherited from the base FileSystem test suite.
/// </summary>
public class InMemoryFileSystemStandardTests : FileSystemStandardTests
{
    public override IFileSystem GetFileSystem()
    {
        var factory = new FileSystemFactoryBuilder()
            .AddInMemoryFileSystem(options =>
            {
                options.Size = Size.FromGigabytes(1);
                options.RootPath = "/";
            })
            .Build();

        return factory.Create("InMemoryFileSystem");
    }
}

/// <summary>
/// Comprehensive tests for the in-memory file system using FHS (Filesystem Hierarchy Standard) conventions.
/// FHS defines the standard directory layout for Unix-like operating systems.
/// </summary>
public class InMemoryFileSystemTests
{
    private IFileSystem CreateFileSystem(long sizeMb = 256, string rootPath = "/", bool isReadOnly = false)
    {
        var factory = new FileSystemFactoryBuilder()
            .AddInMemoryFileSystem(options =>
            {
                options.Size = Size.FromMegabytes(sizeMb);
                options.RootPath = rootPath;
                options.IsReadOnly = isReadOnly;
            })
            .Build();

        return factory.Create("InMemoryFileSystem");
    }

    // ================================================================
    // Factory and Initialization Tests
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Factory: Should create file system via factory builder")]
    public void Factory_Create_ShouldReturnFileSystem()
    {
        var fileSystem = CreateFileSystem();

        Assert.NotNull(fileSystem);
        Assert.Equal("InMemoryFileSystem", fileSystem.Name);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Factory: Should create with custom name")]
    public void Factory_Create_WithCustomName()
    {
        var factory = new FileSystemFactoryBuilder()
            .AddInMemoryFileSystem(options =>
            {
                options.Name = "TestFS";
                options.Size = Size.FromMegabytes(64);
            })
            .Build();

        var fileSystem = factory.Create("InMemoryFileSystem");

        Assert.Equal("TestFS", fileSystem.Name);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Properties: Should report correct size")]
    public void Properties_Size_ShouldMatchOptions()
    {
        var fileSystem = CreateFileSystem(sizeMb: 128);

        Assert.Equal(Size.FromMegabytes(128), fileSystem.Size);
        Assert.Equal(Size.FromMegabytes(128), fileSystem.SpaceAvailable);
        Assert.Equal(0, fileSystem.SpaceUsed.Length);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Properties: RootDirectory should use configured root")]
    public void Properties_RootDirectory_ShouldUseConfiguredRoot()
    {
        var fileSystem = CreateFileSystem(rootPath: "/");

        Assert.NotNull(fileSystem.RootDirectory);
    }

    // ================================================================
    // FHS Directory Structure Tests
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - FHS: Should create /bin directory")]
    public void FHS_CreateBin_ShouldSucceed()
    {
        var fileSystem = CreateFileSystem();

        var bin = fileSystem.CreateDirectory("bin");

        Assert.NotNull(bin);
        Assert.True(fileSystem.Exists("bin"));
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - FHS: Should create standard directory hierarchy")]
    public void FHS_CreateStandardHierarchy_ShouldSucceed()
    {
        var fileSystem = CreateFileSystem();

        // Create standard FHS directories
        fileSystem.CreateDirectory("bin");
        fileSystem.CreateDirectory("boot");
        fileSystem.CreateDirectory("dev");
        fileSystem.CreateDirectory("etc");
        fileSystem.CreateDirectory("home");
        fileSystem.CreateDirectory("lib");
        fileSystem.CreateDirectory("media");
        fileSystem.CreateDirectory("mnt");
        fileSystem.CreateDirectory("opt");
        fileSystem.CreateDirectory("proc");
        fileSystem.CreateDirectory("root");
        fileSystem.CreateDirectory("run");
        fileSystem.CreateDirectory("sbin");
        fileSystem.CreateDirectory("srv");
        fileSystem.CreateDirectory("sys");
        fileSystem.CreateDirectory("tmp");
        fileSystem.CreateDirectory("usr");
        fileSystem.CreateDirectory("var");

        // Verify all exist
        Assert.True(fileSystem.Exists("bin"));
        Assert.True(fileSystem.Exists("boot"));
        Assert.True(fileSystem.Exists("dev"));
        Assert.True(fileSystem.Exists("etc"));
        Assert.True(fileSystem.Exists("home"));
        Assert.True(fileSystem.Exists("lib"));
        Assert.True(fileSystem.Exists("media"));
        Assert.True(fileSystem.Exists("mnt"));
        Assert.True(fileSystem.Exists("opt"));
        Assert.True(fileSystem.Exists("proc"));
        Assert.True(fileSystem.Exists("root"));
        Assert.True(fileSystem.Exists("run"));
        Assert.True(fileSystem.Exists("sbin"));
        Assert.True(fileSystem.Exists("srv"));
        Assert.True(fileSystem.Exists("sys"));
        Assert.True(fileSystem.Exists("tmp"));
        Assert.True(fileSystem.Exists("usr"));
        Assert.True(fileSystem.Exists("var"));
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - FHS: Should create /usr subdirectories")]
    public void FHS_CreateUsrSubdirs_ShouldSucceed()
    {
        var fileSystem = CreateFileSystem();

        // /usr hierarchy per FHS
        fileSystem.CreateDirectory("usr");
        fileSystem.CreateDirectory("usr/bin");
        fileSystem.CreateDirectory("usr/lib");
        fileSystem.CreateDirectory("usr/local");
        fileSystem.CreateDirectory("usr/local/bin");
        fileSystem.CreateDirectory("usr/local/lib");
        fileSystem.CreateDirectory("usr/local/share");
        fileSystem.CreateDirectory("usr/share");
        fileSystem.CreateDirectory("usr/sbin");

        Assert.True(fileSystem.Exists("usr/bin"));
        Assert.True(fileSystem.Exists("usr/lib"));
        Assert.True(fileSystem.Exists("usr/local/bin"));
        Assert.True(fileSystem.Exists("usr/local/lib"));
        Assert.True(fileSystem.Exists("usr/local/share"));
        Assert.True(fileSystem.Exists("usr/share"));
        Assert.True(fileSystem.Exists("usr/sbin"));
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - FHS: Should create /var subdirectories")]
    public void FHS_CreateVarSubdirs_ShouldSucceed()
    {
        var fileSystem = CreateFileSystem();

        // /var hierarchy per FHS
        fileSystem.CreateDirectory("var");
        fileSystem.CreateDirectory("var/cache");
        fileSystem.CreateDirectory("var/lib");
        fileSystem.CreateDirectory("var/log");
        fileSystem.CreateDirectory("var/run");
        fileSystem.CreateDirectory("var/spool");
        fileSystem.CreateDirectory("var/tmp");

        Assert.True(fileSystem.Exists("var/cache"));
        Assert.True(fileSystem.Exists("var/lib"));
        Assert.True(fileSystem.Exists("var/log"));
        Assert.True(fileSystem.Exists("var/run"));
        Assert.True(fileSystem.Exists("var/spool"));
        Assert.True(fileSystem.Exists("var/tmp"));
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - FHS: Should create /etc config files")]
    public void FHS_CreateEtcConfigFiles_ShouldSucceed()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.CreateDirectory("etc");

        // Common config files
        var hostname = fileSystem.CreateFile("etc/hostname");
        var hosts = fileSystem.CreateFile("etc/hosts");
        var fstab = fileSystem.CreateFile("etc/fstab");
        var passwd = fileSystem.CreateFile("etc/passwd");
        var group = fileSystem.CreateFile("etc/group");

        Assert.NotNull(hostname);
        Assert.NotNull(hosts);
        Assert.NotNull(fstab);
        Assert.NotNull(passwd);
        Assert.NotNull(group);

        // Write content to /etc/hostname
        using (var stream = hostname.Open(FileMode.Open, FileAccess.Write))
        {
            var content = Encoding.UTF8.GetBytes("cohesion-host");
            stream.Write(content, 0, content.Length);
        }

        // Verify file size
        Assert.True(hostname.Size.Length > 0);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - FHS: Should create /home user directories")]
    public void FHS_CreateHomeUserDirs_ShouldSucceed()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.CreateDirectory("home");
        fileSystem.CreateDirectory("home/user1");
        fileSystem.CreateDirectory("home/user1/.config");
        fileSystem.CreateDirectory("home/user1/.local");
        fileSystem.CreateDirectory("home/user1/.local/share");
        fileSystem.CreateDirectory("home/user1/Documents");
        fileSystem.CreateDirectory("home/user1/Downloads");

        Assert.True(fileSystem.Exists("home/user1"));
        Assert.True(fileSystem.Exists("home/user1/.config"));
        Assert.True(fileSystem.Exists("home/user1/.local/share"));
        Assert.True(fileSystem.Exists("home/user1/Documents"));
        Assert.True(fileSystem.Exists("home/user1/Downloads"));
    }

    // ================================================================
    // CreateDirectory Tests
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - CreateDirectory: Should throw on duplicate")]
    public void CreateDirectory_Duplicate_ShouldThrowConflict()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.CreateDirectory("test");

        var ex = Assert.Throws<FileSystemException>(() => fileSystem.CreateDirectory("test"));
        Assert.Equal(FileSystemErrorCode.Conflict, ex.Code);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - CreateDirectory: Should auto-create intermediate directories")]
    public void CreateDirectory_DeepNested_ShouldAutoCreate()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.CreateDirectory("a/b/c/d");

        Assert.True(fileSystem.Exists("a"));
        Assert.True(fileSystem.Exists("a/b"));
        Assert.True(fileSystem.Exists("a/b/c"));
        Assert.True(fileSystem.Exists("a/b/c/d"));
    }

    // ================================================================
    // CreateFile Tests
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - CreateFile: Should create file at root")]
    public void CreateFile_AtRoot_ShouldCreate()
    {
        var fileSystem = CreateFileSystem();

        var file = fileSystem.CreateFile("test.txt");

        Assert.NotNull(file);
        Assert.True(fileSystem.Exists("test.txt"));
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - CreateFile: Should create file in nested directory")]
    public void CreateFile_InNested_ShouldAutoCreateDirs()
    {
        var fileSystem = CreateFileSystem();

        var file = fileSystem.CreateFile("var/log/syslog.txt");

        Assert.NotNull(file);
        Assert.True(fileSystem.Exists("var"));
        Assert.True(fileSystem.Exists("var/log"));
        Assert.True(fileSystem.Exists("var/log/syslog.txt"));
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - CreateFile: Should throw on duplicate")]
    public void CreateFile_Duplicate_ShouldThrowConflict()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.CreateFile("test.txt");

        var ex = Assert.Throws<FileSystemException>(() => fileSystem.CreateFile("test.txt"));
        Assert.Equal(FileSystemErrorCode.Conflict, ex.Code);
    }

    // ================================================================
    // File Read/Write Tests
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - File: Should write and read content")]
    public void File_WriteAndRead_ShouldRoundTrip()
    {
        var fileSystem = CreateFileSystem();

        var file = fileSystem.CreateFile("etc/motd");
        var content = Encoding.UTF8.GetBytes("Welcome to Cohesion InMemory FS!");

        using (var stream = file.Open(FileMode.Open, FileAccess.Write))
        {
            stream.Write(content, 0, content.Length);
        }

        var readBuffer = new byte[content.Length];
        using (var stream = file.Open(FileMode.Open, FileAccess.Read))
        {
            stream.Read(readBuffer, 0, readBuffer.Length);
        }

        Assert.Equal(content, readBuffer);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - File: Should support append mode")]
    public void File_AppendMode_ShouldAppendContent()
    {
        var fileSystem = CreateFileSystem();

        var file = fileSystem.CreateFile("var/log/messages");

        var line1 = Encoding.UTF8.GetBytes("First line\n");
        using (var stream = file.Open(FileMode.Open, FileAccess.Write))
        {
            stream.Write(line1, 0, line1.Length);
        }

        var line2 = Encoding.UTF8.GetBytes("Second line\n");
        using (var stream = file.Open(FileMode.Append, FileAccess.Write))
        {
            stream.Write(line2, 0, line2.Length);
        }

        var readBuffer = new byte[line1.Length + line2.Length];
        using (var stream = file.Open(FileMode.Open, FileAccess.Read))
        {
            stream.Read(readBuffer, 0, readBuffer.Length);
        }

        var result = Encoding.UTF8.GetString(readBuffer);
        Assert.Contains("First line", result);
        Assert.Contains("Second line", result);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - File: Should support truncate mode")]
    public void File_TruncateMode_ShouldClearContent()
    {
        var fileSystem = CreateFileSystem();

        var file = fileSystem.CreateFile("tmp/data.bin");

        var content = new byte[100];
        Array.Fill(content, (byte)0xFF);

        using (var stream = file.Open(FileMode.Open, FileAccess.Write))
        {
            stream.Write(content, 0, content.Length);
        }

        Assert.True(file.Size.Length > 0);

        using (var stream = file.Open(FileMode.Truncate, FileAccess.Write))
        {
            // Truncate clears content
        }

        Assert.Equal(0, file.Size.Length);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - File: Should track size after writes")]
    public void File_Size_ShouldTrackWrites()
    {
        var fileSystem = CreateFileSystem();

        var file = fileSystem.CreateFile("tmp/sized.dat");

        Assert.Equal(0, file.Size.Length);

        var content = new byte[1024];
        using (var stream = file.Open(FileMode.Open, FileAccess.Write))
        {
            stream.Write(content, 0, content.Length);
        }

        Assert.Equal(1024, file.Size.Length);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - File: Append with Read should throw")]
    public void File_AppendWithRead_ShouldThrow()
    {
        var fileSystem = CreateFileSystem();
        var file = fileSystem.CreateFile("test.txt");

        Assert.Throws<ArgumentException>(() =>
        {
            file.Open(FileMode.Append, FileAccess.Read);
        });
    }

    // ================================================================
    // Stream Tests
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Stream: Should support seeking")]
    public void Stream_Seek_ShouldPositionCorrectly()
    {
        var fileSystem = CreateFileSystem();
        var file = fileSystem.CreateFile("tmp/seektest.dat");

        var content = Encoding.UTF8.GetBytes("ABCDEFGHIJ");
        using (var stream = file.Open(FileMode.Open, FileAccess.Write))
        {
            stream.Write(content, 0, content.Length);
        }

        using (var stream = file.Open(FileMode.Open, FileAccess.Read))
        {
            stream.Seek(5, SeekOrigin.Begin);

            var buffer = new byte[5];
            stream.Read(buffer, 0, 5);

            Assert.Equal("FGHIJ", Encoding.UTF8.GetString(buffer));
        }
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Stream: Should dispose properly")]
    public void Stream_Dispose_ShouldReleaseLock()
    {
        var fileSystem = CreateFileSystem();
        var file = fileSystem.CreateFile("tmp/lock.dat");

        using (var stream = file.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var content = new byte[] { 1, 2, 3 };
            stream.Write(content, 0, content.Length);
        }

        // Should be able to open again after dispose
        using (var stream = file.Open(FileMode.Open, FileAccess.Read))
        {
            Assert.True(stream.CanRead);
        }
    }

    // ================================================================
    // DeleteDirectory Tests
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - DeleteDirectory: Should delete empty directory")]
    public void DeleteDirectory_Empty_ShouldDelete()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.CreateDirectory("tmp/empty");

        fileSystem.DeleteDirectory("tmp/empty");

        Assert.False(fileSystem.Exists("tmp/empty"));
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - DeleteDirectory: Should delete directory with contents")]
    public void DeleteDirectory_WithContents_ShouldDeleteRecursively()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.CreateDirectory("tmp/parent");
        fileSystem.CreateDirectory("tmp/parent/child");
        fileSystem.CreateFile("tmp/parent/file.txt");
        fileSystem.CreateFile("tmp/parent/child/nested.txt");

        fileSystem.DeleteDirectory("tmp/parent");

        Assert.False(fileSystem.Exists("tmp/parent"));
        Assert.False(fileSystem.Exists("tmp/parent/child"));
        Assert.False(fileSystem.Exists("tmp/parent/file.txt"));
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - DeleteDirectory: Should throw on non-existent")]
    public void DeleteDirectory_NonExistent_ShouldThrow()
    {
        var fileSystem = CreateFileSystem();

        Assert.Throws<FileSystemException>(() => fileSystem.DeleteDirectory("nonexistent"));
    }

    // ================================================================
    // DeleteFile Tests
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - DeleteFile: Should delete file")]
    public void DeleteFile_Existing_ShouldDelete()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.CreateFile("tmp/todelete.txt");
        Assert.True(fileSystem.Exists("tmp/todelete.txt"));

        fileSystem.DeleteFile("tmp/todelete.txt");

        Assert.False(fileSystem.Exists("tmp/todelete.txt"));
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - DeleteFile: Should throw on non-existent")]
    public void DeleteFile_NonExistent_ShouldThrow()
    {
        var fileSystem = CreateFileSystem();

        Assert.Throws<FileSystemException>(() => fileSystem.DeleteFile("nonexistent.txt"));
    }

    // ================================================================
    // Exists Tests
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Exists: Should return true for existing directory")]
    public void Exists_ExistingDirectory_ShouldReturnTrue()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.CreateDirectory("etc");

        Assert.True(fileSystem.Exists("etc"));
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Exists: Should return true for existing file")]
    public void Exists_ExistingFile_ShouldReturnTrue()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.CreateFile("etc/hostname");

        Assert.True(fileSystem.Exists("etc/hostname"));
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Exists: Should return false for non-existent path")]
    public void Exists_NonExistent_ShouldReturnFalse()
    {
        var fileSystem = CreateFileSystem();

        Assert.False(fileSystem.Exists("nonexistent"));
        Assert.False(fileSystem.Exists("etc/nonexistent.conf"));
    }

    // ================================================================
    // GetInfo / GetFile / GetDirectory Tests
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - GetFile: Should return file info")]
    public void GetFile_Existing_ShouldReturnFile()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.CreateFile("etc/hosts");

        var file = fileSystem.GetFile("etc/hosts");

        Assert.NotNull(file);
        Assert.Equal("hosts", file.Name.ToString());
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - GetFile: Should throw for non-existent")]
    public void GetFile_NonExistent_ShouldThrow()
    {
        var fileSystem = CreateFileSystem();

        Assert.Throws<FileSystemException>(() => fileSystem.GetFile("nonexistent.txt"));
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - GetDirectory: Should return directory info")]
    public void GetDirectory_Existing_ShouldReturnDirectory()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.CreateDirectory("var/log");

        var dir = fileSystem.GetDirectory("var/log");

        Assert.NotNull(dir);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - GetDirectory: Should throw for non-existent")]
    public void GetDirectory_NonExistent_ShouldThrow()
    {
        var fileSystem = CreateFileSystem();

        Assert.Throws<FileSystemException>(() => fileSystem.GetDirectory("nonexistent"));
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - GetInfo: Should return correct type for file")]
    public void GetInfo_File_ShouldReturnFileType()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.CreateFile("tmp/info.txt");

        var info = fileSystem.GetInfo("tmp/info.txt");

        Assert.IsAssignableFrom<IFileSystemFile>(info);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - GetInfo: Should return correct type for directory")]
    public void GetInfo_Directory_ShouldReturnDirectoryType()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.CreateDirectory("opt");

        var info = fileSystem.GetInfo("opt");

        Assert.IsAssignableFrom<IFileSystemDirectory>(info);
    }

    // ================================================================
    // CopyFile Tests
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - CopyFile: Should copy file content")]
    public void CopyFile_ShouldCopyContent()
    {
        var fileSystem = CreateFileSystem();

        var source = fileSystem.CreateFile("etc/original.conf");
        var content = Encoding.UTF8.GetBytes("key=value");

        using (var stream = source.Open(FileMode.Open, FileAccess.Write))
        {
            stream.Write(content, 0, content.Length);
        }

        fileSystem.CopyFile("etc/original.conf", "etc/copy.conf");

        Assert.True(fileSystem.Exists("etc/copy.conf"));
        Assert.True(fileSystem.Exists("etc/original.conf"));

        var copy = fileSystem.GetFile("etc/copy.conf");
        var readBuffer = new byte[content.Length];
        using (var stream = copy.Open(FileMode.Open, FileAccess.Read))
        {
            stream.Read(readBuffer, 0, readBuffer.Length);
        }

        Assert.Equal(content, readBuffer);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - CopyFile: Should throw for non-existent source")]
    public void CopyFile_NonExistentSource_ShouldThrow()
    {
        var fileSystem = CreateFileSystem();

        Assert.Throws<FileSystemException>(() =>
            fileSystem.CopyFile("nonexistent.txt", "dest.txt"));
    }

    // ================================================================
    // Move Tests
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Move: Should move file to new location")]
    public void Move_File_ShouldRelocate()
    {
        var fileSystem = CreateFileSystem();

        var file = fileSystem.CreateFile("tmp/moveme.txt");
        var content = Encoding.UTF8.GetBytes("move this");

        using (var stream = file.Open(FileMode.Open, FileAccess.Write))
        {
            stream.Write(content, 0, content.Length);
        }

        fileSystem.Move("tmp/moveme.txt", "var/moved.txt");

        Assert.False(fileSystem.Exists("tmp/moveme.txt"));
        Assert.True(fileSystem.Exists("var/moved.txt"));
    }

    // ================================================================
    // Enumeration Tests
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Enumerate: Should list root entries")]
    public void Enumerate_Root_ShouldListEntries()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.CreateDirectory("bin");
        fileSystem.CreateDirectory("etc");
        fileSystem.CreateDirectory("tmp");

        var entries = fileSystem.EnumerateFileSystem().ToList();

        Assert.Equal(3, entries.Count);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Enumerate: Directory should list its children")]
    public void Enumerate_Directory_ShouldListChildren()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.CreateDirectory("etc");
        fileSystem.CreateFile("etc/hostname");
        fileSystem.CreateFile("etc/hosts");
        fileSystem.CreateFile("etc/fstab");

        var etcDir = fileSystem.GetDirectory("etc");
        var files = etcDir.GetFiles().ToList();

        Assert.Equal(3, files.Count);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Enumerate: GetDirectories should return only directories")]
    public void Enumerate_GetDirectories_ShouldReturnOnlyDirs()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.CreateDirectory("home");
        fileSystem.CreateDirectory("home/user1");
        fileSystem.CreateDirectory("home/user2");
        fileSystem.CreateFile("home/README");

        var homeDir = fileSystem.GetDirectory("home");
        var dirs = homeDir.GetDirectories().ToList();

        Assert.Equal(2, dirs.Count);
    }

    // ================================================================
    // ReadOnly Tests
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - ReadOnly: Should throw on write operations")]
    public void ReadOnly_WriteOperations_ShouldThrow()
    {
        var fileSystem = CreateFileSystem(isReadOnly: true);

        Assert.True(fileSystem.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => fileSystem.CreateDirectory("test"));
        Assert.Throws<InvalidOperationException>(() => fileSystem.CreateFile("test.txt"));
        Assert.Throws<InvalidOperationException>(() => fileSystem.DeleteDirectory("test"));
        Assert.Throws<InvalidOperationException>(() => fileSystem.DeleteFile("test.txt"));
    }

    // ================================================================
    // Space Tracking Tests
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Space: SpaceUsed should increase after writes")]
    public void Space_AfterWrite_ShouldIncreaseUsed()
    {
        var fileSystem = CreateFileSystem(sizeMb: 1);

        var initialUsed = fileSystem.SpaceUsed.Length;

        var file = fileSystem.CreateFile("data.bin");
        var content = new byte[1024];

        using (var stream = file.Open(FileMode.Open, FileAccess.Write))
        {
            stream.Write(content, 0, content.Length);
        }

        Assert.True(fileSystem.SpaceUsed.Length > initialUsed);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Space: SpaceAvailable should decrease after writes")]
    public void Space_AfterWrite_ShouldDecreaseAvailable()
    {
        var fileSystem = CreateFileSystem(sizeMb: 1);

        var initialAvailable = fileSystem.SpaceAvailable.Length;

        var file = fileSystem.CreateFile("data.bin");
        var content = new byte[1024];

        using (var stream = file.Open(FileMode.Open, FileAccess.Write))
        {
            stream.Write(content, 0, content.Length);
        }

        Assert.True(fileSystem.SpaceAvailable.Length < initialAvailable);
    }

    // ================================================================
    // Timestamps Tests
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Timestamps: Should have valid creation time")]
    public void Timestamps_CreatedOn_ShouldBeRecent()
    {
        var fileSystem = CreateFileSystem();

        var before = DateTime.Now.AddSeconds(-1);
        var file = fileSystem.CreateFile("tmp/timed.txt");
        var after = DateTime.Now.AddSeconds(1);

        Assert.True(file.CreatedOn >= before);
        Assert.True(file.CreatedOn <= after);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Timestamps: UpdatedOn should change on write")]
    public void Timestamps_UpdatedOn_ShouldChangeOnWrite()
    {
        var fileSystem = CreateFileSystem();

        var file = fileSystem.CreateFile("tmp/timestamps.txt");
        var createdTime = file.UpdatedOn;

        // Small delay to ensure timestamp difference
        System.Threading.Thread.Sleep(10);

        using (var stream = file.Open(FileMode.Open, FileAccess.Write))
        {
            var content = new byte[] { 1, 2, 3 };
            stream.Write(content, 0, content.Length);
        }

        Assert.True(file.UpdatedOn >= createdTime);
    }

    // ================================================================
    // SetAttributes Tests
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - SetAttributes: Should set and get file attributes")]
    public void SetAttributes_ShouldPersist()
    {
        var fileSystem = CreateFileSystem();

        var file = fileSystem.CreateFile("etc/readonly.conf");
        file.SetAttributes(FileAttributes.ReadOnly);

        Assert.True((file.Attributes & FileAttributes.ReadOnly) != 0);
    }

    // ================================================================
    // Directory Operations Tests
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Directory: CreateFile via directory should work")]
    public void Directory_CreateFile_ShouldWork()
    {
        var fileSystem = CreateFileSystem();

        var dir = fileSystem.CreateDirectory("tmp");

        var file = dir.CreateFile("test.txt");

        Assert.NotNull(file);
        Assert.True(fileSystem.Exists("tmp/test.txt"));
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Directory: CreateDirectory via directory should work")]
    public void Directory_CreateDirectory_ShouldWork()
    {
        var fileSystem = CreateFileSystem();

        var dir = fileSystem.CreateDirectory("home");

        var child = dir.CreateDirectory("user1");

        Assert.NotNull(child);
        Assert.True(fileSystem.Exists("home/user1"));
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Directory: GetFile via directory should work")]
    public void Directory_GetFile_ShouldReturnFile()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.CreateDirectory("etc");
        fileSystem.CreateFile("etc/hostname");

        var dir = fileSystem.GetDirectory("etc");
        var file = dir.GetFile("hostname");

        Assert.NotNull(file);
        Assert.Equal("hostname", file.Name.ToString());
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Directory: DeleteFile via directory should work")]
    public void Directory_DeleteFile_ShouldDelete()
    {
        var fileSystem = CreateFileSystem();

        var dir = fileSystem.CreateDirectory("tmp");
        dir.CreateFile("todelete.txt");

        Assert.True(fileSystem.Exists("tmp/todelete.txt"));

        dir.DeleteFile("todelete.txt");

        Assert.False(fileSystem.Exists("tmp/todelete.txt"));
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Directory: Parent should be set")]
    public void Directory_Parent_ShouldBeSet()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.CreateDirectory("home/user1");

        var user1Dir = fileSystem.GetDirectory("home/user1");

        Assert.NotNull(user1Dir.Parent);
    }

    // ================================================================
    // Dispose Tests
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Dispose: Should not throw")]
    public void Dispose_ShouldNotThrow()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.CreateDirectory("tmp");
        fileSystem.CreateFile("tmp/test.txt");

        fileSystem.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Dispose: Should throw on use after dispose")]
    public void Dispose_UseAfter_ShouldThrow()
    {
        var fileSystem = CreateFileSystem();
        fileSystem.Dispose();

        Assert.Throws<ObjectDisposedException>(() => fileSystem.CreateDirectory("test"));
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Dispose: Double dispose should not throw")]
    public void Dispose_Double_ShouldNotThrow()
    {
        var fileSystem = CreateFileSystem();

        fileSystem.Dispose();
        fileSystem.Dispose();
    }

    // ================================================================
    // Network Share Root Path Test
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - NetworkRoot: Should support UNC-style paths")]
    public void NetworkRoot_UncPath_ShouldWork()
    {
        var factory = new FileSystemFactoryBuilder()
            .AddInMemoryFileSystem(options =>
            {
                options.Size = Size.FromMegabytes(64);
                options.RootPath = "//share/data";
            })
            .Build();

        var fileSystem = factory.Create("InMemoryFileSystem");

        fileSystem.CreateDirectory("documents");
        fileSystem.CreateFile("documents/report.txt");

        Assert.True(fileSystem.Exists("documents"));
        Assert.True(fileSystem.Exists("documents/report.txt"));
    }

    // ================================================================
    // Options Validation Tests
    // ================================================================

    [Fact(DisplayName = "Cohesion Test [InMemoryFileSystem] - Options: Should reject relative root path")]
    public void Options_RelativeRootPath_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            var options = new InMemoryFileSystemOptions();
            options.RootPath = "../relative";
        });
    }
}
