using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.FileSystem.Aggregate.Tests;

/// <summary>
/// Aggregate-specific behavior that is NOT covered by the shared contract suite. Covers mount
/// routing (longest-prefix, synthetic root, overlap rules), cross-provider copy/move, watch
/// fan-in with path remapping, and disposal ownership semantics.
/// </summary>
public class AggregateFileSystemTests
{
    // -----------------------------------------------------------------
    // Mount registration
    // -----------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - Builder: rejects empty mount path")]
    public void Builder_RejectsEmptyMountPath()
    {
        var builder = new AggregateFileSystemBuilder();
        Assert.Throws<ArgumentException>(() =>
            builder.Mount(FileSystemPath.Empty, new InMemoryFileSystem(new InMemoryFileSystemOptions())));
    }

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - Builder: rejects null file system")]
    public void Builder_RejectsNullFileSystem()
    {
        var builder = new AggregateFileSystemBuilder();
        Assert.Throws<ArgumentNullException>(() => builder.Mount("/data", null!));
    }

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - Builder: rejects duplicate mount path")]
    public void Builder_RejectsDuplicateMountPath()
    {
        using var fs1 = new InMemoryFileSystem(new InMemoryFileSystemOptions());
        using var fs2 = new InMemoryFileSystem(new InMemoryFileSystemOptions());

        var builder = new AggregateFileSystemBuilder().Mount("/data", fs1);

        Assert.Throws<ArgumentException>(() => builder.Mount("/data", fs2));
    }

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - Builder: rejects use after Build")]
    public void Builder_SingleUse()
    {
        var builder = new AggregateFileSystemBuilder()
            .Mount("/data", new InMemoryFileSystem(new InMemoryFileSystemOptions()), ownsFileSystem: true);
        using var fs = builder.Build();

        Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Throws<InvalidOperationException>(() =>
            builder.Mount("/cache", new InMemoryFileSystem(new InMemoryFileSystemOptions())));
    }

    // -----------------------------------------------------------------
    // Longest-prefix resolution
    // -----------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - Routing: longest prefix wins for nested mounts")]
    public void Routing_LongestPrefixWins()
    {
        var outer = new InMemoryFileSystem(new InMemoryFileSystemOptions());
        var inner = new InMemoryFileSystem(new InMemoryFileSystemOptions());

        using var fs = new AggregateFileSystemBuilder()
            .Mount("/data", outer, ownsFileSystem: true)
            .Mount("/data/cache", inner, ownsFileSystem: true)
            .Build();

        // A file under /data/cache must land on the inner provider, NOT the outer one even
        // though "/data" is also a valid prefix.
        fs.CreateFile("/data/cache/hot.bin");
        Assert.True(inner.Exists("/hot.bin"));
        Assert.False(outer.Exists("/cache/hot.bin"));
    }

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - Routing: prefix check requires segment boundary")]
    public void Routing_PrefixMatchRequiresSegmentBoundary()
    {
        // "/data" must not match "/database" — that's a different segment.
        var dataFs = new InMemoryFileSystem(new InMemoryFileSystemOptions());

        using var fs = new AggregateFileSystemBuilder()
            .Mount("/data", dataFs, ownsFileSystem: true)
            .Build();

        Assert.False(fs.Exists("/database"));
        // /data itself exists (synthetic-or-mount root).
        Assert.True(fs.Exists("/data"));
    }

    // -----------------------------------------------------------------
    // Synthetic root
    // -----------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - Synthetic root: intermediates surface for traversal")]
    public void Synthetic_IntermediatesAppear()
    {
        // Only "/data/cache" is mounted — "/data" is synthetic.
        var cache = new InMemoryFileSystem(new InMemoryFileSystemOptions());
        using var fs = new AggregateFileSystemBuilder()
            .Mount("/data/cache", cache, ownsFileSystem: true)
            .Build();

        Assert.True(fs.Exists("/data"));        // synthetic
        Assert.True(fs.Exists("/data/cache")); // mount root

        var data = fs.GetDirectory("/data");
        var children = data.GetDirectories().Select(d => d.Path.ToString()).ToList();
        Assert.Contains("/data/cache", children);
    }

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - Synthetic root: write operations throw ReadOnly")]
    public void Synthetic_Writes_ReadOnly()
    {
        var cache = new InMemoryFileSystem(new InMemoryFileSystemOptions());
        using var fs = new AggregateFileSystemBuilder()
            .Mount("/data/cache", cache, ownsFileSystem: true)
            .Build();

        var exception = Assert.Throws<FileSystemException>(() => fs.CreateFile("/data/orphan.txt"));
        Assert.Equal(FileSystemErrorCode.ReadOnly, exception.Code);

        var exception2 = Assert.Throws<FileSystemException>(() => fs.CreateDirectory("/something-new"));
        Assert.Equal(FileSystemErrorCode.ReadOnly, exception2.Code);
    }

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - Synthetic root: name is '/' and Parent is null")]
    public void Synthetic_RootName()
    {
        var cache = new InMemoryFileSystem(new InMemoryFileSystemOptions());
        using var fs = new AggregateFileSystemBuilder()
            .Mount("/data/cache", cache, ownsFileSystem: true)
            .Build();

        Assert.Equal("/", fs.RootDirectory.Name.ToString());
        Assert.Null(fs.RootDirectory.Parent);
    }

    // -----------------------------------------------------------------
    // CRUD delegation
    // -----------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - CRUD: create + read + delete delegate to mount")]
    public void Crud_DelegatesToMount()
    {
        var dataFs = new InMemoryFileSystem(new InMemoryFileSystemOptions());

        using var fs = new AggregateFileSystemBuilder()
            .Mount("/data", dataFs, ownsFileSystem: true)
            .Build();

        var file = fs.CreateFile("/data/notes.txt");
        Assert.Equal("/data/notes.txt", file.Path.ToString());

        using (var stream = file.Open(FileMode.Open, FileAccess.Write))
        {
            stream.Write(Encoding.UTF8.GetBytes("payload"));
        }

        // Underlying provider sees the path relative to its own root.
        Assert.True(dataFs.Exists("/notes.txt"));

        var readBack = new byte[7];
        using (var stream = fs.GetFile("/data/notes.txt").Open(FileMode.Open, FileAccess.Read))
        {
            stream.ReadExactly(readBack);
        }
        Assert.Equal("payload", Encoding.UTF8.GetString(readBack));

        fs.DeleteFile("/data/notes.txt");
        Assert.False(fs.Exists("/data/notes.txt"));
        Assert.False(dataFs.Exists("/notes.txt"));
    }

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - GetFile: paths in returned info are aggregate-rooted")]
    public void GetFile_PathsAreAggregateRooted()
    {
        var dataFs = new InMemoryFileSystem(new InMemoryFileSystemOptions());

        using var fs = new AggregateFileSystemBuilder()
            .Mount("/data", dataFs, ownsFileSystem: true)
            .Build();

        fs.CreateFile("/data/notes.txt");
        var file = fs.GetFile("/data/notes.txt");

        // Aggregate-space path, not "/notes.txt" from the underlying provider.
        Assert.Equal("/data/notes.txt", file.Path.ToString());
        Assert.Equal("/data", file.Directory.Path.ToString());
        Assert.Same(fs, file.FileSystem);
    }

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - GetFile/Directory: missing path throws NotFound")]
    public void Get_Missing_NotFound()
    {
        var dataFs = new InMemoryFileSystem(new InMemoryFileSystemOptions());

        using var fs = new AggregateFileSystemBuilder()
            .Mount("/data", dataFs, ownsFileSystem: true)
            .Build();

        Assert.Equal(FileSystemErrorCode.NotFound,
            Assert.Throws<FileSystemException>(() => fs.GetFile("/data/missing.txt")).Code);
        Assert.Equal(FileSystemErrorCode.NotFound,
            Assert.Throws<FileSystemException>(() => fs.GetDirectory("/data/missing")).Code);
    }

    // -----------------------------------------------------------------
    // Cross-provider Copy / Move
    // -----------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - CopyFile: cross-provider clones content")]
    public void CopyFile_CrossProvider_ClonesContent()
    {
        var dataFs = new InMemoryFileSystem(new InMemoryFileSystemOptions());
        var cacheFs = new InMemoryFileSystem(new InMemoryFileSystemOptions());

        using var fs = new AggregateFileSystemBuilder()
            .Mount("/data", dataFs, ownsFileSystem: true)
            .Mount("/cache", cacheFs, ownsFileSystem: true)
            .Build();

        var src = fs.CreateFile("/data/src.bin");
        var payload = Encoding.UTF8.GetBytes("cross-provider");
        using (var stream = src.Open(FileMode.Open, FileAccess.Write))
        {
            stream.Write(payload);
        }

        fs.CopyFile("/data/src.bin", "/cache/copy.bin");

        Assert.True(dataFs.Exists("/src.bin"));
        Assert.True(cacheFs.Exists("/copy.bin"));

        var readBack = new byte[payload.Length];
        using (var stream = cacheFs.GetFile("/copy.bin").Open(FileMode.Open, FileAccess.Read))
        {
            stream.ReadExactly(readBack);
        }
        Assert.Equal(payload, readBack);
    }

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - Move: cross-provider relocates content")]
    public void Move_CrossProvider_Relocates()
    {
        var dataFs = new InMemoryFileSystem(new InMemoryFileSystemOptions());
        var archiveFs = new InMemoryFileSystem(new InMemoryFileSystemOptions());

        using var fs = new AggregateFileSystemBuilder()
            .Mount("/data", dataFs, ownsFileSystem: true)
            .Mount("/archive", archiveFs, ownsFileSystem: true)
            .Build();

        var src = fs.CreateFile("/data/movable.txt");
        var payload = Encoding.UTF8.GetBytes("hello");
        using (var stream = src.Open(FileMode.Open, FileAccess.Write))
        {
            stream.Write(payload);
        }

        fs.Move("/data/movable.txt", "/archive/moved.txt");

        Assert.False(fs.Exists("/data/movable.txt"));
        Assert.True(fs.Exists("/archive/moved.txt"));
        Assert.False(dataFs.Exists("/movable.txt"));
        Assert.True(archiveFs.Exists("/moved.txt"));
    }

    // -----------------------------------------------------------------
    // Enumeration
    // -----------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - Enumerate: lists synthetic + mount entries from root")]
    public void Enumerate_Root_ListsMountsAndSynthetics()
    {
        var dataFs = new InMemoryFileSystem(new InMemoryFileSystemOptions());
        var nestedFs = new InMemoryFileSystem(new InMemoryFileSystemOptions());

        using var fs = new AggregateFileSystemBuilder()
            .Mount("/data", dataFs, ownsFileSystem: true)
            .Mount("/var/log", nestedFs, ownsFileSystem: true)
            .Build();

        var rootChildren = fs.RootDirectory.GetDirectories().Select(d => d.Path.ToString()).ToList();

        Assert.Contains("/data", rootChildren);
        Assert.Contains("/var", rootChildren); // synthetic ancestor of /var/log
    }

    // -----------------------------------------------------------------
    // Disposal ownership
    // -----------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - Dispose: cascades when ownsFileSystem is true")]
    public void Dispose_Cascades_WhenOwned()
    {
        var owned = new TrackedFileSystem("owned");
        var external = new TrackedFileSystem("external");

        var fs = new AggregateFileSystemBuilder()
            .Mount("/owned", owned, ownsFileSystem: true)
            .Mount("/external", external, ownsFileSystem: false)
            .Build();

        fs.Dispose();

        Assert.True(owned.IsDisposed);
        Assert.False(external.IsDisposed);
    }

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - Dispose: is idempotent")]
    public void Dispose_Idempotent()
    {
        var owned = new TrackedFileSystem("owned");

        var fs = new AggregateFileSystemBuilder()
            .Mount("/owned", owned, ownsFileSystem: true)
            .Build();

        fs.Dispose();
        fs.Dispose();

        Assert.Equal(1, owned.DisposeCount);
    }

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - Dispose: rejects use after dispose")]
    public void UseAfterDispose_Throws()
    {
        var inner = new InMemoryFileSystem(new InMemoryFileSystemOptions());
        var fs = new AggregateFileSystemBuilder()
            .Mount("/data", inner, ownsFileSystem: true)
            .Build();
        fs.Dispose();

        Assert.Throws<ObjectDisposedException>(() => fs.Exists("/data/foo"));
        Assert.Throws<ObjectDisposedException>(() => fs.CreateFile("/data/foo"));
    }

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - DisposeAsync: cascades when ownsFileSystem is true")]
    public async Task DisposeAsync_Cascades()
    {
        var owned = new TrackedFileSystem("owned");

        var fs = new AggregateFileSystemBuilder()
            .Mount("/owned", owned, ownsFileSystem: true)
            .Build();

        await fs.DisposeAsync();

        Assert.True(owned.IsDisposed);
    }

    // -----------------------------------------------------------------
    // Read-only mode
    // -----------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - ReadOnly: rejects every write across all mounts")]
    public void ReadOnly_BlocksAllWrites()
    {
        var dataFs = new InMemoryFileSystem(new InMemoryFileSystemOptions());

        using var fs = new AggregateFileSystemBuilder()
            .AsReadOnly()
            .Mount("/data", dataFs, ownsFileSystem: true)
            .Build();

        AssertReadOnly(() => fs.CreateFile("/data/x.txt"));
        AssertReadOnly(() => fs.CreateDirectory("/data/x"));
        AssertReadOnly(() => fs.DeleteFile("/data/x.txt"));
        AssertReadOnly(() => fs.DeleteDirectory("/data/x"));
        AssertReadOnly(() => fs.CopyFile("/data/a", "/data/b"));
        AssertReadOnly(() => fs.Move("/data/a", "/data/b"));

        static void AssertReadOnly(Action action)
        {
            var ex = Assert.Throws<FileSystemException>(action);
            Assert.Equal(FileSystemErrorCode.ReadOnly, ex.Code);
        }
    }

    // -----------------------------------------------------------------
    // Watch fan-in
    // -----------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - Watch: fan-in surfaces mount events through aggregate")]
    public async Task Watch_FanInFiresCallback()
    {
        // The fan-in token subscribes to every mount and forwards events with remapped paths.
        // The exact remapped string is covered by AggregateRouterTests.Mount_ToAggregatePath;
        // here we only assert that a mount-side change reaches an aggregate-level subscriber.
        var dataFs = new InMemoryFileSystem(new InMemoryFileSystemOptions());

        using var fs = new AggregateFileSystemBuilder()
            .Mount("/data", dataFs, ownsFileSystem: true)
            .Build();

        var token = fs.Watch(null);

        int callbacks = 0;
        using var reg = token.OnCreate<object?>(_ => System.Threading.Interlocked.Increment(ref callbacks), state: null);

        fs.CreateFile("/data/observed.txt");

        await WaitFor(() => callbacks > 0, TimeSpan.FromSeconds(1));
        Assert.True(callbacks > 0, "Aggregate fan-in should surface at least one OnCreate callback from the mounted provider.");
    }

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - Watch: aggregate-level glob filters out non-matching events")]
    public async Task Watch_FanInRespectsAggregateGlob()
    {
        var dataFs = new InMemoryFileSystem(new InMemoryFileSystemOptions());
        using var fs = new AggregateFileSystemBuilder()
            .Mount("/data", dataFs, ownsFileSystem: true)
            .Build();

        // Pattern that requires a ".log" extension. /data/audit.txt should be filtered out at
        // the aggregate layer regardless of whether it's filtered at the mount level.
        var token = fs.Watch(Glob.Parse("**/*.log"));

        int matches = 0;
        using var reg = token.OnCreate<object?>(_ => System.Threading.Interlocked.Increment(ref matches), state: null);

        fs.CreateFile("/data/audit.txt");
        await Task.Delay(150);

        Assert.Equal(0, matches);
    }

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - Watch: dispose cascades to mount tokens")]
    public void Watch_Dispose_CleansMountTokens()
    {
        var dataFs = new InMemoryFileSystem(new InMemoryFileSystemOptions());

        using var fs = new AggregateFileSystemBuilder()
            .Mount("/data", dataFs, ownsFileSystem: true)
            .Build();

        var token = fs.Watch(null);
        ((IDisposable)token).Dispose();

        // After dispose, registrations succeed but never fire — confirmed by the no-op
        // contract in the underlying token. The hard invariant is no exception is thrown.
        var reg = token.OnCreate<object?>(_ => { }, state: null);
        reg.Dispose();
    }

    // -----------------------------------------------------------------
    // Factory builder extension
    // -----------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Test [AggregateFileSystem] - Factory: AddAggregateFileSystem registers")]
    public void Factory_AddAggregate_Registers()
    {
        using var factory = new FileSystemFactoryBuilder()
            .AddAggregateFileSystem(builder =>
            {
                builder.Mount("/data", new InMemoryFileSystem(new InMemoryFileSystemOptions()), ownsFileSystem: true);
            })
            .Build();

        Assert.Contains("AggregateFileSystem", factory.Names);
        var fs = factory.Create("AggregateFileSystem");
        Assert.IsType<AggregateFileSystem>(fs);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(25);
        }
    }

    private sealed class TrackedFileSystem : IFileSystem
    {
        private readonly InMemoryFileSystem _inner;

        public TrackedFileSystem(string name)
        {
            _inner = new InMemoryFileSystem(new InMemoryFileSystemOptions { Name = name });
        }

        public int DisposeCount { get; private set; }
        public bool IsDisposed => DisposeCount > 0;

        public Size Size => _inner.Size;
        public Size SpaceAvailable => _inner.SpaceAvailable;
        public Size SpaceUsed => _inner.SpaceUsed;
        public string Name => _inner.Name;
        public bool IsReadOnly => _inner.IsReadOnly;
        public IFileSystemDirectory RootDirectory => _inner.RootDirectory;
        public bool Exists(FileSystemPath path) => _inner.Exists(path);
        public IFileSystemEventToken Watch(Glob? pattern) => _inner.Watch(pattern);
        public System.Collections.Generic.IEnumerable<IFileSystemInfo> EnumerateFileSystem(FileSystemEnumerationOptions? options = null)
            => _inner.EnumerateFileSystem(options);
        public IFileSystemDirectory GetDirectory(FileSystemPath path) => _inner.GetDirectory(path);
        public IFileSystemFile GetFile(FileSystemPath path) => _inner.GetFile(path);
        public IFileSystemInfo GetInfo(FileSystemPath path) => _inner.GetInfo(path);
        public IFileSystemDirectory CreateDirectory(FileSystemPath path) => _inner.CreateDirectory(path);
        public IFileSystemFile CreateFile(FileSystemPath path) => _inner.CreateFile(path);
        public void DeleteDirectory(FileSystemPath path) => _inner.DeleteDirectory(path);
        public void DeleteFile(FileSystemPath path) => _inner.DeleteFile(path);
        public void CopyFile(FileSystemPath source, FileSystemPath destination) => _inner.CopyFile(source, destination);
        public void Move(FileSystemPath source, FileSystemPath destination) => _inner.Move(source, destination);
        public System.Collections.Generic.IEnumerator<IFileSystemInfo> GetEnumerator() => _inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose() { DisposeCount++; _inner.Dispose(); }
        public ValueTask DisposeAsync() { DisposeCount++; return _inner.DisposeAsync(); }
    }
}
