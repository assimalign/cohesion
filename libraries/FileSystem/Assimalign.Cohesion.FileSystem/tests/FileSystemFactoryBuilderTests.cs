using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.FileSystem.Tests;

public class FileSystemFactoryBuilderTests
{
    [Fact(DisplayName = "Cohesion Test [FileSystem] - Builder: AddFileSystem rejects null/empty name")]
    public void AddFileSystem_RejectsEmptyName()
    {
        var builder = new FileSystemFactoryBuilder();
        var fs = new StubFileSystem("stub");

        Assert.Throws<ArgumentException>(() => builder.AddFileSystem("", fs));
        Assert.Throws<ArgumentNullException>(() => builder.AddFileSystem(null!, fs));
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - Builder: AddFileSystem rejects null instance")]
    public void AddFileSystem_RejectsNullInstance()
    {
        var builder = new FileSystemFactoryBuilder();

        Assert.Throws<ArgumentNullException>(() => builder.AddFileSystem("name", (IFileSystem)null!));
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - Builder: rejects duplicate name")]
    public void AddFileSystem_RejectsDuplicateName()
    {
        var builder = new FileSystemFactoryBuilder();
        builder.AddFileSystem("dup", new StubFileSystem("a"));

        Assert.Throws<InvalidOperationException>(() => builder.AddFileSystem("dup", new StubFileSystem("b")));
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - Builder: duplicate detection is case-insensitive")]
    public void AddFileSystem_DuplicateNameCaseInsensitive()
    {
        var builder = new FileSystemFactoryBuilder();
        builder.AddFileSystem("Stub", new StubFileSystem("a"));

        Assert.Throws<InvalidOperationException>(() => builder.AddFileSystem("STUB", new StubFileSystem("b")));
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - Builder: cannot be reused after Build")]
    public void Build_SingleUse()
    {
        var builder = new FileSystemFactoryBuilder();
        builder.AddFileSystem("x", new StubFileSystem("x"));
        using var factory = builder.Build();

        Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Throws<InvalidOperationException>(() => builder.AddFileSystem("y", new StubFileSystem("y")));
        Assert.Throws<InvalidOperationException>(() => builder.AddFileSystem<StubFileSystem>("y", () => new StubFileSystem("y")));
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - Builder: typed factory uses type name")]
    public void AddFileSystem_TypedFactory_UsesTypeName()
    {
        using var factory = new FileSystemFactoryBuilder()
            .AddFileSystem<StubFileSystem>(() => new StubFileSystem("stub"))
            .Build();

        Assert.Contains("StubFileSystem", factory.Names);
        var resolved = factory.Create("StubFileSystem");
        Assert.IsType<StubFileSystem>(resolved);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - Builder: AddFileSystem typed with explicit name")]
    public void AddFileSystem_TypedFactory_WithExplicitName()
    {
        using var factory = new FileSystemFactoryBuilder()
            .AddFileSystem<StubFileSystem>("Custom", () => new StubFileSystem("stub"))
            .Build();

        Assert.Contains("Custom", factory.Names);
        Assert.IsType<StubFileSystem>(factory.Create("Custom"));
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - Factory: Create throws FileSystemException for unknown name")]
    public void Factory_Create_UnknownName_Throws()
    {
        using var factory = new FileSystemFactoryBuilder()
            .AddFileSystem("known", new StubFileSystem("k"))
            .Build();

        var exception = Assert.Throws<FileSystemException>(() => factory.Create("unknown"));
        Assert.Equal(FileSystemErrorCode.NotFound, exception.Code);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - Factory: Create<T> throws FileSystemException when no matching type")]
    public void Factory_CreateT_NoMatch_Throws()
    {
        using var factory = new FileSystemFactoryBuilder()
            .AddFileSystem("stub", new StubFileSystem("s"))
            .Build();

        var exception = Assert.Throws<FileSystemException>(() => factory.Create<OtherStubFileSystem>());
        Assert.Equal(FileSystemErrorCode.NotFound, exception.Code);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - Factory: Create<T> returns matching instance")]
    public void Factory_CreateT_ReturnsMatching()
    {
        using var factory = new FileSystemFactoryBuilder()
            .AddFileSystem<StubFileSystem>(() => new StubFileSystem("a"))
            .Build();

        var resolved = factory.Create<StubFileSystem>();
        Assert.IsType<StubFileSystem>(resolved);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - Factory: Create returns same instance on repeat calls")]
    public void Factory_Create_ReturnsSameInstance()
    {
        using var factory = new FileSystemFactoryBuilder()
            .AddFileSystem<StubFileSystem>(() => new StubFileSystem("once"))
            .Build();

        var first = factory.Create("StubFileSystem");
        var second = factory.Create("StubFileSystem");
        Assert.Same(first, second);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - Factory: name lookup is case-insensitive")]
    public void Factory_Create_NameCaseInsensitive()
    {
        using var factory = new FileSystemFactoryBuilder()
            .AddFileSystem("Mixed", new StubFileSystem("m"))
            .Build();

        Assert.NotNull(factory.Create("MIXED"));
        Assert.NotNull(factory.Create("mixed"));
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - Factory: Dispose disposes every materialized file system")]
    public void Factory_Dispose_CascadesDispose()
    {
        var a = new StubFileSystem("a");
        var b = new StubFileSystem("b");
        var factory = new FileSystemFactoryBuilder()
            .AddFileSystem("a", a)
            .AddFileSystem("b", b)
            .Build();

        // Materialize both by name (already eager via the instance overload).
        _ = factory.Create("a");
        _ = factory.Create("b");

        factory.Dispose();

        Assert.True(a.IsDisposed);
        Assert.True(b.IsDisposed);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - Factory: Dispose is idempotent")]
    public void Factory_Dispose_Idempotent()
    {
        var fs = new StubFileSystem("a");
        var factory = new FileSystemFactoryBuilder()
            .AddFileSystem("a", fs)
            .Build();
        _ = factory.Create("a");

        factory.Dispose();
        factory.Dispose();

        Assert.Equal(1, fs.DisposeCount);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - Factory: Use after dispose throws")]
    public void Factory_UseAfterDispose_Throws()
    {
        var factory = new FileSystemFactoryBuilder()
            .AddFileSystem("a", new StubFileSystem("a"))
            .Build();
        factory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => factory.Create("a"));
        Assert.Throws<ObjectDisposedException>(() => factory.Create<StubFileSystem>());
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - Factory: DisposeAsync cascades")]
    public async Task Factory_DisposeAsync_Cascades()
    {
        var a = new StubFileSystem("a");
        var factory = new FileSystemFactoryBuilder()
            .AddFileSystem("a", a)
            .Build();
        _ = factory.Create("a");

        await factory.DisposeAsync();

        Assert.True(a.IsDisposed);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - Factory: lazy factory does not invoke until first Create")]
    public void Factory_LazyFactory_DefersInvocation()
    {
        int invocations = 0;

        using var factory = new FileSystemFactoryBuilder()
            .AddFileSystem<StubFileSystem>(() =>
            {
                invocations++;
                return new StubFileSystem("lazy");
            })
            .Build();

        Assert.Equal(0, invocations);

        _ = factory.Create("StubFileSystem");
        _ = factory.Create("StubFileSystem");

        Assert.Equal(1, invocations);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - Factory: Names returns registered names")]
    public void Factory_Names_ReturnsRegisteredNames()
    {
        using var factory = new FileSystemFactoryBuilder()
            .AddFileSystem("alpha", new StubFileSystem("a"))
            .AddFileSystem("beta", new StubFileSystem("b"))
            .Build();

        Assert.Equal(2, factory.Names.Count);
        Assert.Contains("alpha", factory.Names);
        Assert.Contains("beta", factory.Names);
    }

    private class StubFileSystem : IFileSystem
    {
        public StubFileSystem(string id)
        {
            Name = id;
        }

        public int DisposeCount { get; private set; }
        public bool IsDisposed => DisposeCount > 0;

        public Size Size => default;
        public Size SpaceAvailable => default;
        public Size SpaceUsed => default;
        public string Name { get; }
        public bool IsReadOnly => false;
        public IFileSystemDirectory RootDirectory => throw new NotImplementedException();

        public bool Exists(FileSystemPath path) => false;
        public IFileSystemEventToken Watch(Glob? pattern) => throw new NotImplementedException();
        public System.Collections.Generic.IEnumerable<IFileSystemInfo> EnumerateFileSystem(FileSystemEnumerationOptions? options = null)
            => System.Array.Empty<IFileSystemInfo>();
        public IFileSystemDirectory GetDirectory(FileSystemPath path) => throw new NotImplementedException();
        public IFileSystemFile GetFile(FileSystemPath path) => throw new NotImplementedException();
        public IFileSystemInfo GetInfo(FileSystemPath path) => throw new NotImplementedException();
        public IFileSystemDirectory CreateDirectory(FileSystemPath path) => throw new NotImplementedException();
        public IFileSystemFile CreateFile(FileSystemPath path) => throw new NotImplementedException();
        public void DeleteDirectory(FileSystemPath path) => throw new NotImplementedException();
        public void DeleteFile(FileSystemPath path) => throw new NotImplementedException();
        public void CopyFile(FileSystemPath source, FileSystemPath destination) => throw new NotImplementedException();
        public void Move(FileSystemPath source, FileSystemPath destination) => throw new NotImplementedException();

        public System.Collections.Generic.IEnumerator<IFileSystemInfo> GetEnumerator() => EnumerateFileSystem().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose() => DisposeCount++;
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OtherStubFileSystem : StubFileSystem
    {
        public OtherStubFileSystem() : base("other") { }
    }
}
