using System;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Storage;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentStorageDurabilityTests
{
    [Theory]
    [InlineData(false, null, StorageCommitDurability.None)]
    [InlineData(true, null, StorageCommitDurability.Synchronous)]
    [InlineData(true, StorageCommitDurability.Grouped, StorageCommitDurability.Grouped)]
    [InlineData(true, StorageCommitDurability.None, StorageCommitDurability.None)]
    public async Task DatabaseOpen_ShouldResolveDurabilityFromStorage(bool physical, StorageCommitDurability? configured, StorageCommitDurability expected)
    {
        string directory = Path.Combine(Path.GetTempPath(), "cohesion-Document-storage-durability", Guid.NewGuid().ToString("N"));
        var options = new DocumentDatabaseEngineOptions { RootPath = physical ? FileSystemPath.Parse(directory) : (FileSystemPath?)null, Durability = configured };
        try
        {
            await using (var engine = DocumentDatabaseEngine.Create(options))
            {
                var database = (DocumentDatabaseInstance)await engine.CreateDatabaseAsync("db");
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
                await using var reopened = DocumentDatabaseEngine.Create(options);
                var database = (DocumentDatabaseInstance)await reopened.OpenDatabaseAsync("db");
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
        await using var engine = DocumentDatabaseEngine.Create(new DocumentDatabaseEngineOptions { Durability = durability });
        var failure = await Should.ThrowAsync<NotSupportedException>(async () => await engine.CreateDatabaseAsync("memory-store"));
        failure.Message.ShouldContain("DocumentStorage");
        failure.Message.ShouldContain("memory-store");
        failure.Message.ShouldContain(durability.ToString());
        engine.TryGetDatabase("memory-store", out _).ShouldBeFalse();
        engine.State.ShouldBe(EngineState.Running);
    }
}
