using System;
using System.IO;
using System.IO.IsolatedStorage;
using System.Linq;
using System.Text;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.FileSystem.Isolated.Tests;

/// <summary>
/// Provider-specific behavior that is NOT covered by the shared contract suite. Includes options
/// validation, the read-only enforcement contract, the noop watch token, and the unsupported
/// capabilities (Attributes / SetAttributes).
/// </summary>
public class IsolatedFileSystemTests
{
    [Fact(DisplayName = "Cohesion Test [IsolatedFileSystem] - Constructor: null options throws")]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new IsolatedFileSystem(null!));
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedFileSystem] - Constructor: default options succeed")]
    public void Constructor_Default_Succeeds()
    {
        IsolatedFileSystemTestFixture.ClearUserStoreForAssembly();
        using var fs = new IsolatedFileSystem();
        Assert.Equal(nameof(IsolatedFileSystem), fs.Name);
        Assert.False(fs.IsReadOnly);
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedFileSystem] - Name: honors options.Name")]
    public void Name_HonorsOption()
    {
        IsolatedFileSystemTestFixture.ClearUserStoreForAssembly();
        using var fs = new IsolatedFileSystem(new IsolatedFileSystemOptions { Name = "MyStore" });
        Assert.Equal("MyStore", fs.Name);
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedFileSystem] - IsReadOnly: blocks every write operation")]
    public void IsReadOnly_BlocksWrites()
    {
        // Seed an entry through a writable view first so the read-only system has something to act on.
        using (var seed = IsolatedFileSystemTestFixture.CreateFreshFileSystem())
        {
            seed.CreateFile("seed.txt");
            seed.CreateDirectory("seedDir");
        }

        // Re-open the same store in read-only mode (do NOT clear it first).
        using var fs = new IsolatedFileSystem(new IsolatedFileSystemOptions { IsReadOnly = true });
        Assert.True(fs.IsReadOnly);

        AssertReadOnly(() => fs.CreateDirectory("blocked"));
        AssertReadOnly(() => fs.CreateFile("blocked.txt"));
        AssertReadOnly(() => fs.DeleteDirectory("seedDir"));
        AssertReadOnly(() => fs.DeleteFile("seed.txt"));
        AssertReadOnly(() => fs.CopyFile("seed.txt", "copy.txt"));
        AssertReadOnly(() => fs.Move("seed.txt", "moved.txt"));

        static void AssertReadOnly(Action action)
        {
            var exception = Assert.Throws<FileSystemException>(action);
            Assert.Equal(FileSystemErrorCode.ReadOnly, exception.Code);
        }
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedFileSystem] - Watch: returns the noop token")]
    public void Watch_ReturnsNoopToken()
    {
        using var fs = IsolatedFileSystemTestFixture.CreateFreshFileSystem();

        var token = fs.Watch(null);

        // Registering callbacks must succeed without throwing, and the returned IDisposable must
        // be safe to dispose. The token never fires, so we only assert the contract surface.
        var reg1 = token.OnChange(_ => { }, state: (object?)null);
        var reg2 = token.OnCreate<string>(_ => { }, state: null);
        var reg3 = token.OnDelete<string>(_ => { }, state: null);
        var reg4 = token.OnRename<string>(_ => { }, state: null);

        reg1.Dispose();
        reg2.Dispose();
        reg3.Dispose();
        reg4.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedFileSystem] - File: Watch returns the noop token")]
    public void File_Watch_ReturnsNoopToken()
    {
        using var fs = IsolatedFileSystemTestFixture.CreateFreshFileSystem();
        var file = fs.CreateFile("note.txt");

        var token = file.Watch();
        Assert.NotNull(token);

        // The token should not fire even after the file is mutated.
        bool fired = false;
        using var reg = token.OnChange<object?>(_ => fired = true, state: null);

        using (var stream = file.Open(FileMode.Open, FileAccess.Write))
        {
            stream.Write(Encoding.UTF8.GetBytes("payload"));
        }

        Assert.False(fired, "IsolatedFileSystem watch token must be a noop.");
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedFileSystem] - Attributes: getter throws NotSupported")]
    public void Attributes_Get_Throws()
    {
        using var fs = IsolatedFileSystemTestFixture.CreateFreshFileSystem();
        var file = fs.CreateFile("attr.txt");

        Assert.Throws<NotSupportedException>(() => file.Attributes);
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedFileSystem] - SetAttributes throws NotSupported")]
    public void SetAttributes_Throws()
    {
        using var fs = IsolatedFileSystemTestFixture.CreateFreshFileSystem();
        var file = fs.CreateFile("attr.txt");

        Assert.Throws<NotSupportedException>(() => file.SetAttributes(FileAttributes.ReadOnly));
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedFileSystem] - Dispose: file system rejects use after dispose")]
    public void UseAfterDispose_Throws()
    {
        var fs = IsolatedFileSystemTestFixture.CreateFreshFileSystem();
        fs.Dispose();

        Assert.Throws<ObjectDisposedException>(() => fs.CreateFile("after.txt"));
        Assert.Throws<ObjectDisposedException>(() => fs.Exists("missing.txt"));
        Assert.Throws<ObjectDisposedException>(() => fs.RootDirectory);
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedFileSystem] - Dispose: idempotent")]
    public void Dispose_Idempotent()
    {
        var fs = IsolatedFileSystemTestFixture.CreateFreshFileSystem();
        fs.Dispose();
        fs.Dispose(); // second call must not throw
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedFileSystem] - RemoveStoreOnDispose: nukes backing store")]
    public void RemoveStoreOnDispose_ClearsStore()
    {
        IsolatedFileSystemTestFixture.ClearUserStoreForAssembly();

        using (var fs = new IsolatedFileSystem(new IsolatedFileSystemOptions { RemoveStoreOnDispose = true }))
        {
            fs.CreateFile("trace.txt");
            Assert.True(fs.Exists("trace.txt"));
        }

        // After dispose the store should be removed; opening a fresh view sees no entries.
        using var store = IsolatedStorageFile.GetUserStoreForAssembly();
        Assert.Empty(store.GetFileNames("*"));
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedFileSystem] - Size: surfaces Quota / UsedSize / AvailableFreeSpace")]
    public void Sizes_AreReported()
    {
        using var fs = IsolatedFileSystemTestFixture.CreateFreshFileSystem();
        Assert.True(fs.Size.Length > 0);
        Assert.True(fs.SpaceAvailable.Length >= 0);
        // SpaceUsed is implementation-defined (some stores return 0). Just verify it doesn't throw.
        _ = fs.SpaceUsed;
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedFileSystem] - RootDirectory: name is '/'")]
    public void Root_Name_IsSlash()
    {
        using var fs = IsolatedFileSystemTestFixture.CreateFreshFileSystem();
        Assert.Equal("/", fs.RootDirectory.Name.ToString());
        Assert.Null(fs.RootDirectory.Parent);
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedFileSystem] - Factory: AddIsolatedFileSystem registers provider")]
    public void Factory_AddIsolatedFileSystem_Registers()
    {
        IsolatedFileSystemTestFixture.ClearUserStoreForAssembly();

        using var factory = new FileSystemFactoryBuilder()
            .AddIsolatedFileSystem()
            .Build();

        Assert.Contains("IsolatedFileSystem", factory.Names);

        var resolved = factory.Create("IsolatedFileSystem");
        Assert.IsType<IsolatedFileSystem>(resolved);
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedFileSystem] - Factory: AddIsolatedFileSystem(configure) passes options")]
    public void Factory_AddIsolatedFileSystem_Configure_PassesOptions()
    {
        IsolatedFileSystemTestFixture.ClearUserStoreForAssembly();

        using var factory = new FileSystemFactoryBuilder()
            .AddIsolatedFileSystem(options =>
            {
                options.Name = "Configured";
                options.IsReadOnly = true;
            })
            .Build();

        var fs = factory.Create("IsolatedFileSystem");
        Assert.Equal("Configured", fs.Name);
        Assert.True(fs.IsReadOnly);
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedFileSystem] - Directory.GetFiles + GetDirectories list children")]
    public void Directory_Children_AreListed()
    {
        using var fs = IsolatedFileSystemTestFixture.CreateFreshFileSystem();
        fs.CreateDirectory("root");
        fs.CreateDirectory("root/sub1");
        fs.CreateDirectory("root/sub2");
        fs.CreateFile("root/payload.bin");

        var root = fs.GetDirectory("root");
        var files = root.GetFiles().ToList();
        var dirs = root.GetDirectories().ToList();

        Assert.Single(files);
        Assert.Equal("payload.bin", files[0].Name.ToString());
        Assert.Equal(2, dirs.Count);
    }
}
