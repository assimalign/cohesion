using System;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Storage;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphStorageDurabilityTests
{
    [Theory]
    [InlineData(false, null, StorageCommitDurability.None)]
    [InlineData(true, null, StorageCommitDurability.Synchronous)]
    [InlineData(true, StorageCommitDurability.Grouped, StorageCommitDurability.Grouped)]
    [InlineData(true, StorageCommitDurability.None, StorageCommitDurability.None)]
    public async Task DatabaseOpen_ShouldResolveDurabilityFromStorage(bool physical, StorageCommitDurability? configured, StorageCommitDurability expected)
    {
        string directory = Path.Combine(Path.GetTempPath(), "cohesion-Graph-storage-durability", Guid.NewGuid().ToString("N"));
        var options = new GraphDatabaseEngineOptions { RootPath = physical ? directory : null, Durability = configured };
        try
        {
            await using (var engine = GraphDatabaseEngine.Create(options))
            {
                var database = (GraphDatabaseInstance)await engine.CreateDatabaseAsync("db");
                database.DataStorage.SupportsDurableFlush.ShouldBe(physical);
                database.DataStorage.CommitDurability.ShouldBe(expected);
                using var transaction = database.DataStorage.BeginTransaction();
                transaction.Commit();
                if (expected == StorageCommitDurability.None)
                {
                    database.DataStorage.WriteAheadJournal.DurableLsn.ShouldBe(0);
                }
                else
                {
                    database.DataStorage.WriteAheadJournal.DurableLsn.ShouldBeGreaterThan(0);
                }
            }
            if (physical)
            {
                await using var reopened = GraphDatabaseEngine.Create(options);
                var database = (GraphDatabaseInstance)await reopened.OpenDatabaseAsync("db");
                database.DataStorage.CommitDurability.ShouldBe(expected);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(StorageCommitDurability.Synchronous)]
    [InlineData(StorageCommitDurability.Grouped)]
    public async Task ExplicitDurabilityOnMemory_ShouldRejectBeforeDatabasePublication(StorageCommitDurability durability)
    {
        await using var engine = GraphDatabaseEngine.Create(new GraphDatabaseEngineOptions { Durability = durability });
        var failure = await Should.ThrowAsync<NotSupportedException>(async () => await engine.CreateDatabaseAsync("memory-store"));
        failure.Message.ShouldContain("GraphStorage");
        failure.Message.ShouldContain("memory-store");
        failure.Message.ShouldContain(durability.ToString());
        engine.TryGetDatabase("memory-store", out _).ShouldBeFalse();
        engine.State.ShouldBe(EngineState.Running);
    }
}
