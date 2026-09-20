using System;
using System.IO;
using System.IO.IsolatedStorage;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;

using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.FileSystem.IsolatedStorage.Tests;

/// <summary>
/// Provider-specific behavior that is NOT covered by the shared contract suite. Includes options
/// validation, the read-only enforcement contract, the noop watch token, and the unsupported
/// capabilities (Attributes / SetAttributes).
/// </summary>
public class IsolatedStorageFileSystemTests
{
    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Constructor: null options throws")]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new IsolatedStorageFileSystem(null!));
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Constructor: default options succeed")]
    public void Constructor_Default_Succeeds()
    {
        IsolatedStorageFileSystemTestFixture.ClearUserStoreForAssembly();
        using var fs = new IsolatedStorageFileSystem();
        Assert.Equal(nameof(IsolatedStorageFileSystem), fs.Name);
        Assert.False(fs.IsReadOnly);
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Name: honors options.Name")]
    public void Name_HonorsOption()
    {
        IsolatedStorageFileSystemTestFixture.ClearUserStoreForAssembly();
        using var fs = new IsolatedStorageFileSystem(new IsolatedStorageFileSystemOptions { Name = "MyStore" });
        Assert.Equal("MyStore", fs.Name);
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - IsReadOnly: blocks every write operation")]
    public void IsReadOnly_BlocksWrites()
    {
        // Seed an entry through a writable view first so the read-only system has something to act on.
        using (var seed = IsolatedStorageFileSystemTestFixture.CreateFreshFileSystem())
        {
            seed.CreateFile("seed.txt");
            seed.CreateDirectory("seedDir");
        }

        // Re-open the same store in read-only mode (do NOT clear it first).
        using var fs = new IsolatedStorageFileSystem(new IsolatedStorageFileSystemOptions { IsReadOnly = true });
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

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Watch: polling disabled returns noop token")]
    public void Watch_PollingDisabled_ReturnsNoopToken()
    {
        // Setting WatchPollInterval to InfiniteTimeSpan explicitly opts out of polling. The
        // returned token must accept registrations and dispose cleanly without ever firing.
        using var fs = IsolatedStorageFileSystemTestFixture.CreateFreshFileSystem(
            watchPollInterval: System.Threading.Timeout.InfiniteTimeSpan);

        var token = fs.Watch(null);

        var reg1 = token.OnChange(_ => { }, state: (object?)null);
        var reg2 = token.OnCreate<string>(_ => { }, state: null);
        var reg3 = token.OnDelete<string>(_ => { }, state: null);
        var reg4 = token.OnRename<string>(_ => { }, state: null);

        reg1.Dispose();
        reg2.Dispose();
        reg3.Dispose();
        reg4.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Watch: polling fires OnCreate when file appears")]
    public async Task Watch_Polling_FiresOnCreate()
    {
        using var fs = IsolatedStorageFileSystemTestFixture.CreateFreshFileSystem(
            watchPollInterval: TimeSpan.FromMilliseconds(50));

        // End each watch scope explicitly; provider disposal remains a fallback for owned tokens.
        var token = fs.Watch(null);
        using var watchScope = (IDisposable)token;

        var createdPaths = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var reg = token.OnCreate<object?>(e => createdPaths.Add(e.Path.ToString()), state: null);

        // The constructor captures the baseline synchronously.
        fs.CreateFile("appeared.txt");

        await WaitFor(() => createdPaths.Contains("/appeared.txt"), TimeSpan.FromSeconds(5));
        createdPaths.ShouldContain("/appeared.txt");
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Watch: polling fires OnDelete when file removed")]
    public async Task Watch_Polling_FiresOnDelete()
    {
        using var fs = IsolatedStorageFileSystemTestFixture.CreateFreshFileSystem(
            watchPollInterval: TimeSpan.FromMilliseconds(50));
        fs.CreateFile("doomed.txt");

        var token = fs.Watch(null);
        using var watchScope = (IDisposable)token;

        var deletedPaths = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var reg = token.OnDelete<object?>(e => deletedPaths.Add(e.Path.ToString()), state: null);

        fs.DeleteFile("doomed.txt");

        await WaitFor(() => deletedPaths.Contains("/doomed.txt"), TimeSpan.FromSeconds(5));
        deletedPaths.ShouldContain("/doomed.txt");
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Watch: polling fires OnChange when file modified")]
    public async Task Watch_Polling_FiresOnChange()
    {
        using var fs = IsolatedStorageFileSystemTestFixture.CreateFreshFileSystem(
            watchPollInterval: TimeSpan.FromMilliseconds(50));
        var file = fs.CreateFile("mutating.txt");

        var token = fs.Watch(null);
        using var watchScope = (IDisposable)token;

        var changedPaths = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var reg = token.OnChange<object?>(e => changedPaths.Add(e.Path.ToString()), state: null);

        using (var stream = file.Open(FileMode.Open, FileAccess.Write))
        {
            stream.Write(Encoding.UTF8.GetBytes("payload"));
        }

        await WaitFor(() => changedPaths.Contains("/mutating.txt"), TimeSpan.FromSeconds(5));
        changedPaths.ShouldContain("/mutating.txt");
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Watch: glob filter scopes events")]
    public async Task Watch_Polling_HonorsGlobFilter()
    {
        using var fs = IsolatedStorageFileSystemTestFixture.CreateFreshFileSystem(
            watchPollInterval: TimeSpan.FromMilliseconds(50));

        // Only *.log entries should surface through the filtered token.
        var token = fs.Watch(Glob.Parse("**/*.log"));
        using var watchScope = (IDisposable)token;

        var createdPaths = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var reg = token.OnCreate<object?>(e => createdPaths.Add(e.Path.ToString()), state: null);

        fs.CreateFile("audit.log");
        fs.CreateFile("audit.txt");

        await WaitFor(() => createdPaths.Contains("/audit.log"), TimeSpan.FromSeconds(5));
        createdPaths.ShouldContain("/audit.log");
        createdPaths.ShouldNotContain("/audit.txt");
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - File.Watch: polling tracks single file")]
    public async Task File_Watch_Polling_TracksSingleFile()
    {
        using var fs = IsolatedStorageFileSystemTestFixture.CreateFreshFileSystem(
            watchPollInterval: TimeSpan.FromMilliseconds(50));
        var file = fs.CreateFile("note.txt");

        var token = file.Watch();
        using var watchScope = (IDisposable)token;

        var changedPaths = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var reg = token.OnChange<object?>(e => changedPaths.Add(e.Path.ToString()), state: null);

        using (var stream = file.Open(FileMode.Open, FileAccess.Write))
        {
            stream.Write(Encoding.UTF8.GetBytes("payload"));
        }

        await WaitFor(() => changedPaths.Contains("/note.txt"), TimeSpan.FromSeconds(5));
        changedPaths.ShouldContain("/note.txt");
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Watch: dispose stops the timer")]
    public async Task Watch_Dispose_StopsTimer()
    {
        using var fs = IsolatedStorageFileSystemTestFixture.CreateFreshFileSystem(
            watchPollInterval: TimeSpan.FromMilliseconds(50));

        var token = fs.Watch(null);
        using var watchScope = (IDisposable)token;
        int createdCount = 0;
        var reg = token.OnCreate<object?>(_ => Interlocked.Increment(ref createdCount), state: null);

        ((IDisposable)token).Dispose();

        // A second active watch proves polling and the file system still work after the
        // first token's lifetime ends. Dedicated manual-timer tests assert timer shutdown.
        var liveToken = fs.Watch(null);
        using var liveScope = (IDisposable)liveToken;
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var liveRegistration = liveToken.OnCreate<object?>(_ => observed.TrySetResult(), null);
        fs.CreateFile("ignored.txt");
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Read(ref createdCount).ShouldBe(0);
        reg.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Watch: rename registration accepted but never fires")]
    public async Task Watch_OnRename_NeverFires()
    {
        using var fs = IsolatedStorageFileSystemTestFixture.CreateFreshFileSystem(
            watchPollInterval: TimeSpan.FromMilliseconds(50));
        fs.CreateFile("before.txt");

        var token = fs.Watch(null);
        using var watchScope = (IDisposable)token;

        bool renameFired = false;
        using var reg = token.OnRename<object?>(_ => renameFired = true, state: null);

        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deletion = token.OnDelete<object?>(_ => deleted.TrySetResult(), null);
        fs.Move("before.txt", "after.txt");
        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        renameFired.ShouldBeFalse();
    }

    private static async Task WaitFor(System.Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(25, CancellationToken.None);
        }
        condition().ShouldBeTrue("The expected watch event did not arrive before the deadline.");
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Attributes: getter throws NotSupported")]
    public void Attributes_Get_Throws()
    {
        using var fs = IsolatedStorageFileSystemTestFixture.CreateFreshFileSystem();
        var file = fs.CreateFile("attr.txt");

        Assert.Throws<NotSupportedException>(() => file.Attributes);
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - SetAttributes throws NotSupported")]
    public void SetAttributes_Throws()
    {
        using var fs = IsolatedStorageFileSystemTestFixture.CreateFreshFileSystem();
        var file = fs.CreateFile("attr.txt");

        Assert.Throws<NotSupportedException>(() => file.SetAttributes(FileAttributes.ReadOnly));
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Dispose: file system rejects use after dispose")]
    public void UseAfterDispose_Throws()
    {
        var fs = IsolatedStorageFileSystemTestFixture.CreateFreshFileSystem();
        fs.Dispose();

        Assert.Throws<ObjectDisposedException>(() => fs.CreateFile("after.txt"));
        Assert.Throws<ObjectDisposedException>(() => fs.Exists("missing.txt"));
        Assert.Throws<ObjectDisposedException>(() => fs.RootDirectory);
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Dispose: idempotent")]
    public void Dispose_Idempotent()
    {
        var fs = IsolatedStorageFileSystemTestFixture.CreateFreshFileSystem();
        fs.Dispose();
        fs.Dispose(); // second call must not throw
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - RemoveStoreOnDispose: nukes backing store")]
    public void RemoveStoreOnDispose_ClearsStore()
    {
        IsolatedStorageFileSystemTestFixture.ClearUserStoreForAssembly();

        using (var fs = new IsolatedStorageFileSystem(new IsolatedStorageFileSystemOptions { RemoveStoreOnDispose = true }))
        {
            fs.CreateFile("trace.txt");
            Assert.True(fs.Exists("trace.txt"));
        }

        // After dispose the store should be removed; opening a fresh view sees no entries.
        using var store = IsolatedStorageFile.GetUserStoreForAssembly();
        Assert.Empty(store.GetFileNames("*"));
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Size: surfaces Quota / UsedSize / AvailableFreeSpace")]
    public void Sizes_AreReported()
    {
        using var fs = IsolatedStorageFileSystemTestFixture.CreateFreshFileSystem();
        Assert.True(fs.Size.Length > 0);
        Assert.True(fs.SpaceAvailable.Length >= 0);
        // SpaceUsed is implementation-defined (some stores return 0). Just verify it doesn't throw.
        _ = fs.SpaceUsed;
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - RootDirectory: name is '/'")]
    public void Root_Name_IsSlash()
    {
        using var fs = IsolatedStorageFileSystemTestFixture.CreateFreshFileSystem();
        Assert.Equal("/", fs.RootDirectory.Name.ToString());
        Assert.Null(fs.RootDirectory.Parent);
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Factory: AddIsolatedStorageFileSystem registers provider")]
    public void Factory_AddIsolatedStorageFileSystem_Registers()
    {
        IsolatedStorageFileSystemTestFixture.ClearUserStoreForAssembly();

        using var factory = new FileSystemFactoryBuilder()
            .AddIsolatedStorageFileSystem()
            .Build();

        Assert.Contains("IsolatedStorageFileSystem", factory.Names);

        var resolved = factory.Create("IsolatedStorageFileSystem");
        Assert.IsType<IsolatedStorageFileSystem>(resolved);
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Factory: AddIsolatedStorageFileSystem(configure) passes options")]
    public void Factory_AddIsolatedStorageFileSystem_Configure_PassesOptions()
    {
        IsolatedStorageFileSystemTestFixture.ClearUserStoreForAssembly();

        using var factory = new FileSystemFactoryBuilder()
            .AddIsolatedStorageFileSystem(options =>
            {
                options.Name = "Configured";
                options.IsReadOnly = true;
            })
            .Build();

        var fs = factory.Create("IsolatedStorageFileSystem");
        Assert.Equal("Configured", fs.Name);
        Assert.True(fs.IsReadOnly);
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Directory.GetFiles + GetDirectories list children")]
    public void Directory_Children_AreListed()
    {
        using var fs = IsolatedStorageFileSystemTestFixture.CreateFreshFileSystem();
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
