using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.KeyValuePair.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

public sealed class KeyValueInterruptedCreationTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-kv-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath)) { Directory.Delete(_rootPath, recursive: true); }
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Open: Missing in-memory catalog is refused without recreating it (#1238)")]
    public async Task Open_InMemoryDataWithoutCatalog_ShouldRefuseWithoutCreatingCatalog()
    {
        // Arrange: creation stopped after the data set, before the catalog was created.
        var strategy = new InMemoryKeyValueStorageStrategy();
        using (strategy.CreateStorage("kv", catalog: false)) { }
        await using var engine = KeyValueDatabaseEngine.Create("keyvalue-engine", new() { StorageStrategy = strategy });

        // Act / Assert: repeat the open to prove refusal does not repair the missing identity.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var failure = await Record.ExceptionAsync(async () => await engine.OpenDatabaseAsync("kv", CancellationToken.None));
            strategy.StorageExists("kv", catalog: true).ShouldBeFalse();
            strategy.StorageExists("kv", catalog: false).ShouldBeTrue();
            var refusal = failure.ShouldBeOfType<DatabaseException>();
            refusal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBK006");
            refusal.Message.ShouldContain("catalog", Case.Sensitive);
            refusal.Message.ShouldContain("restore", Case.Sensitive);
            refusal.Message.ShouldNotContain(".dat", Case.Sensitive);
            engine.TryGetDatabase("kv", out _).ShouldBeFalse();
        }
        await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync("kv", CancellationToken.None));
        strategy.StorageExists("kv", catalog: true).ShouldBeFalse();
        await engine.DropDatabaseAsync("kv", CancellationToken.None);
        strategy.StorageExists("kv", catalog: false).ShouldBeFalse();
        await engine.CreateDatabaseAsync("kv", CancellationToken.None);
        strategy.StorageExists("kv", catalog: true).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Open: Creation-length data with no catalog names drop and recreate and leaves files unchanged (#1238)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_CreationLengthDataWithoutCatalog_ShouldRefuseWithDropAndRecreateRemedy(bool sidecars)
    {
        // Arrange: only the initial data page reached disk; optionally its sidecars did too.
        string directory = Path.Combine(_rootPath, "kv");
        Directory.CreateDirectory(directory);
        using (KeyValueStorage.Create(
            StorageStream.FromFile(Path.Combine(directory, "keyvalue.dat"), FileMode.CreateNew, FileShare.Read),
            sidecars ? StorageStream.FromFile(Path.Combine(directory, "keyvalue.log"), FileMode.CreateNew, FileShare.Read) : StorageStream.FromInMemory(),
            sidecars ? StorageStream.FromFile(Path.Combine(directory, "keyvalue.bak"), FileMode.CreateNew, FileShare.Read) : StorageStream.FromInMemory(),
            "kv")) { }
        // Creation stopped before the second initial page was flushed (Documents precedent).
        using (var data = File.OpenWrite(Path.Combine(directory, "keyvalue.dat"))) { data.SetLength(Page.Size); }
        new FileInfo(Path.Combine(directory, "keyvalue.dat")).Length.ShouldBe(Page.Size);
        var before = Directory.GetFiles(directory).ToDictionary(path => path, File.ReadAllBytes);
        await using var engine = KeyValueDatabaseEngine.Create("keyvalue-engine", new() { RootPath = _rootPath });

        // Act / Assert
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync("kv", CancellationToken.None));
            failure.Message.ShouldContain("DropDatabaseAsync", Case.Sensitive);
            failure.Message.ShouldContain("CreateDatabaseAsync", Case.Sensitive);
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBK006");
            engine.TryGetDatabase("kv", out _).ShouldBeFalse();
            Directory.GetFiles(directory).ShouldBe(before.Keys, ignoreOrder: true);
            foreach (var (path, bytes) in before) { File.ReadAllBytes(path).ShouldBe(bytes); }
        }
        await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync("kv", CancellationToken.None));
        Directory.GetFiles(directory).ShouldBe(before.Keys, ignoreOrder: true);
        foreach (var (path, bytes) in before) { File.ReadAllBytes(path).ShouldBe(bytes); }
        await engine.DropDatabaseAsync("kv", CancellationToken.None);
        Directory.Exists(directory).ShouldBeFalse();
        await engine.CreateDatabaseAsync("kv", CancellationToken.None);
        File.Exists(Path.Combine(directory, "catalog.dat")).ShouldBeTrue();
    }
}
