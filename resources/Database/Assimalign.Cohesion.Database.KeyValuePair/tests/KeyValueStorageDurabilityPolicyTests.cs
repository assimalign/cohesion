using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using Assimalign.Cohesion.Database.KeyValuePair.Storage;
using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Engine durability follows the backing capability, while explicit durable
/// policies are rejected before the database becomes available.
/// </summary>
public sealed class KeyValueStorageDurabilityPolicyTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(), "cohesion-keyvalue-durability-policy", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(null)]
    [InlineData(StorageCommitDurability.None)]
    public async Task MemoryBacking_WithUnsetOrNoneDurability_ShouldServeWritesWithoutClaimingDurability(
        StorageCommitDurability? durability)
    {
        var options = new KeyValueDatabaseEngineOptions { Durability = durability };
        await using var engine = KeyValueDatabaseEngine.Create(options);
        var database = await engine.CreateDatabaseAsync("memory-default");
        await using var session = await database.CreateSessionAsync();

        var keyValueDatabase = (IKeyValueDatabase)database;
        var put = await keyValueDatabase.PutAsync(session, new byte[] { 1 }, new byte[] { 7 });
        put.Applied.ShouldBeTrue();
        var value = await keyValueDatabase.GetAsync(session, new byte[] { 1 });
        value.ShouldNotBeNull();
        value!.Value.Value.ToArray().ShouldBe(new byte[] { 7 });

        options.Durability.ShouldBe(durability);
        var storages = engine.GetStorageSnapshot();
        storages.Length.ShouldBe(2);
        foreach (var storage in storages)
        {
            storage.SupportsDurableFlush.ShouldBeFalse();
            storage.CommitDurability.ShouldBe(StorageCommitDurability.None);
        }
        engine.State.ShouldBe(EngineState.Running);
    }

    [Theory]
    [InlineData(null, StorageCommitDurability.Synchronous)]
    [InlineData(StorageCommitDurability.Synchronous, StorageCommitDurability.Synchronous)]
    [InlineData(StorageCommitDurability.Grouped, StorageCommitDurability.Grouped)]
    public async Task PhysicalBacking_ShouldDeriveOrHonorDurablePolicy(
        StorageCommitDurability? configured, StorageCommitDurability expected)
    {
        await using var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions
        {
            RootPath = _rootPath,
            Durability = configured,
        });
        await engine.CreateDatabaseAsync("physical-policy");

        var storages = engine.GetStorageSnapshot();
        storages.Length.ShouldBe(2);
        foreach (var storage in storages)
        {
            storage.SupportsDurableFlush.ShouldBeTrue();
            storage.CommitDurability.ShouldBe(expected);
        }
        engine.State.ShouldBe(EngineState.Running);
    }

    [Theory]
    [InlineData(StorageCommitDurability.Synchronous, false)]
    [InlineData(StorageCommitDurability.Grouped, false)]
    [InlineData(StorageCommitDurability.Synchronous, true)]
    [InlineData(StorageCommitDurability.Grouped, true)]
    public async Task NonDurableBacking_WithExplicitDurability_ShouldRejectOpenAndReleaseStorage(
        StorageCommitDurability durability, bool openExisting)
    {
        var strategy = new NonDurableStorageStrategy();
        await using var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions
        {
            StorageStrategy = strategy,
            Durability = durability,
        });

        var exception = await Should.ThrowAsync<NotSupportedException>(async () =>
        {
            if (openExisting)
            {
                await engine.OpenDatabaseAsync("unsupported-store");
            }
            else
            {
                await engine.CreateDatabaseAsync("unsupported-store");
            }
        });

        exception.Message.ShouldContain(nameof(NonDurableStorageStrategy));
        exception.Message.ShouldContain("unsupported-store");
        exception.Message.ShouldContain(durability.ToString());
        engine.GetStorageSnapshot().ShouldBeEmpty();
        engine.TryGetDatabase("unsupported-store", out _).ShouldBeFalse();
        strategy.Streams.Count.ShouldBe(3);
        foreach (var stream in strategy.Streams)
        {
            stream.CanRead.ShouldBeFalse();
        }
        engine.State.ShouldBe(EngineState.Running);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private sealed class NonDurableStorageStrategy : IKeyValueStorageStrategy
    {
        internal List<MemoryStream> Streams { get; } = [];

        public KeyValueStorage CreateStorage(string databaseName)
        {
            var data = new MemoryStream();
            var journal = new MemoryStream();
            var backup = new MemoryStream();
            Streams.AddRange([data, journal, backup]);
            return KeyValueStorage.Create(data, journal, backup, databaseName);
        }

        public KeyValueStorage OpenStorage(string databaseName) => CreateStorage(databaseName);

        public bool StorageExists(string databaseName) => true;

        public void DropStorage(string databaseName) { }
    }
}
