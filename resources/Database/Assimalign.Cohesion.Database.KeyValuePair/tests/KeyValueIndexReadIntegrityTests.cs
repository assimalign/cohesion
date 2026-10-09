using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.Storage;

using static KeyValueTestHarness;

/// <summary>
/// The entries a primary-index seek fetches (#1342). An index entry whose entry record was
/// reclaimed beneath it is stale and reads as absence. An entry page that cannot be read is not
/// reclaimed: before the fix every storage error read as absence, so a page that failed its
/// checksum made its keys disappear from Get, and a Put over such a key resolved it as missing and
/// failed with a retryable write-write conflict that no retry could clear.
/// </summary>
public sealed class KeyValueIndexReadIntegrityTests
{
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Index reads: a Get over an entry page that fails its checksum fails instead of reading the key as missing (#1342)")]
    public async Task Get_EntryPageRotten_ShouldFailWithStorageCorruption()
    {
        // Arrange: the entry page, read once, then evicted so the next read goes to the data file.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = CreateEngine(strategy);
        var database = await engine.CreateDatabaseAsync(DatabaseName, TestTimeout.Token());
        await using var session = await database.CreateSessionAsync();
        await database.PutAsync(session, Bytes("alpha"), Bytes("one"), cancellationToken: TestTimeout.Token());
        await database.PutAsync(session, Bytes("bravo"), Bytes("two"), cancellationToken: TestTimeout.Token());
        var entryPage = database.DataStorage.GetOwnerPages(KeyValueOperationExecutor.KeySpaceObjectId).ShouldHaveSingleItem();
        Evict(database, entryPage);

        // Act: the page decays on the device.
        var faults = strategy.Faults(DatabaseName);
        faults.RottenPage = entryPage;
        var failure = await Should.ThrowAsync<StorageCorruptionException>(() => database.GetAsync(session, Bytes("bravo"), TestTimeout.Token()).AsTask());
        long rottenReads = faults.RottenPageReads;
        faults.RottenPage = null;
        var healed = await database.GetAsync(session, Bytes("bravo"), TestTimeout.Token());

        // Assert
        failure.PageId.ShouldBe(entryPage);
        rottenReads.ShouldBeGreaterThan(0);
        Text(healed.ShouldNotBeNull().Value).ShouldBe("two");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Index reads: a Put over an entry page that fails its checksum fails without writing a version (#1342)")]
    public async Task Put_EntryPageRotten_ShouldFailWithoutWritingAVersion()
    {
        // Arrange: the key's record sits on the first entry page; later records filled it, so
        // the next version would land on another page and the Put reads the first one only to
        // resolve the key's current version.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = CreateEngine(strategy);
        var database = await engine.CreateDatabaseAsync(DatabaseName, TestTimeout.Token());
        await using var session = await database.CreateSessionAsync();
        string padding = new('x', 3_000);
        var original = await database.PutAsync(session, Bytes("alpha"), Bytes("one" + padding), cancellationToken: TestTimeout.Token());
        for (int filler = 0; filler < 3; filler++)
        {
            await database.PutAsync(session, Bytes($"filler-{filler}"), Bytes(padding), cancellationToken: TestTimeout.Token());
        }

        var entryPage = EntryLocation(database, "alpha").PageId;
        database.DataStorage.GetOwnerPages(KeyValueOperationExecutor.KeySpaceObjectId).Count.ShouldBeGreaterThan(1);
        database.DataStorage.GetOwnerPages(KeyValueOperationExecutor.KeySpaceObjectId)[^1].ShouldNotBe(entryPage);
        Evict(database, entryPage);

        // Act
        var faults = strategy.Faults(DatabaseName);
        faults.RottenPage = entryPage;
        await Should.ThrowAsync<StorageCorruptionException>(() => database.PutAsync(session, Bytes("alpha"), Bytes("replaced"), cancellationToken: TestTimeout.Token()).AsTask());
        faults.RottenPage = null;
        var entry = await database.GetAsync(session, Bytes("alpha"), TestTimeout.Token());

        // Assert: the key kept its one version.
        Text(entry.ShouldNotBeNull().Value).ShouldBe("one" + padding);
        entry.Value.ETag.ShouldBe(original.ETag!.Value);
        EntryCount(database).ShouldBe(4);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Index reads: an entry whose record was reclaimed beneath it reads as absent (#1342)")]
    public async Task Get_EntryRecordReclaimedBeneathIndex_ShouldReadAsAbsent()
    {
        // Arrange: two keys on one page; one key's record is deleted beneath its index entry, the
        // state a purge or an undo leaves under an entry a reader already holds.
        await using var engine = CreateEngine(strategy: null);
        var database = await engine.CreateDatabaseAsync(DatabaseName, TestTimeout.Token());
        await using var session = await database.CreateSessionAsync();
        await database.PutAsync(session, Bytes("alpha"), Bytes("one"), cancellationToken: TestTimeout.Token());
        await database.PutAsync(session, Bytes("bravo"), Bytes("two"), cancellationToken: TestTimeout.Token());
        var reclaimed = EntryLocation(database, "alpha");
        using (var bracket = database.DataStorage.BeginTransaction())
        {
            database.DataStorage.DeleteEntry(bracket, reclaimed.PageId, reclaimed.SlotIndex);
            bracket.Commit();
        }

        // Act
        var alpha = await database.GetAsync(session, Bytes("alpha"), TestTimeout.Token());
        var bravo = await database.GetAsync(session, Bytes("bravo"), TestTimeout.Token());

        // Assert
        alpha.ShouldBeNull();
        Text(bravo.ShouldNotBeNull().Value).ShouldBe("two");
        database.DataStorage.FreeSpaceMap.IsAllocated(reclaimed.PageId).ShouldBeTrue();
    }

    private static KeyValueDatabaseEngine CreateEngine(FaultInjectingJournalStorageStrategy? strategy)
        => KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions
        {
            EngineName = "kv-index-reads",
            StorageStrategy = strategy,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });

    private static (PageId PageId, int SlotIndex) EntryLocation(KeyValueDatabase database, string key)
    {
        using var iterator = database.DataStorage.GetUnitIterator(KeyValueOperationExecutor.KeySpaceObjectId);
        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            if (KeyValueRecordCodec.TryDecode(unit.Data.Span, out byte[] stored, out _, out _, out _) && Text(stored) == key)
            {
                return (unit.PageId, unit.SlotIndex);
            }
        }

        throw new InvalidOperationException($"The record of key '{key}' was not found.");
    }

    private static int EntryCount(KeyValueDatabase database)
    {
        using var iterator = database.DataStorage.GetUnitIterator(KeyValueOperationExecutor.KeySpaceObjectId);
        int count = 0;
        while (iterator.MoveNext())
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Evicts a page from the data storage's buffer pool: the pool shrinks to one frame, which
    /// another page then takes, and grows back.
    /// </summary>
    private static void Evict(KeyValueDatabase database, PageId pageId)
    {
        var storage = database.DataStorage;
        var other = Enumerable.Range(1, (int)storage.PageManager.PageCount - 1)
            .Select(page => (PageId)(long)page)
            .First(page => page != pageId && storage.FreeSpaceMap.IsAllocated(page));
        int capacity = storage.BufferPoolCapacity;
        storage.BufferPoolCapacity = 1;
        using (storage.PageManager.GetPage(other))
        {
        }

        storage.BufferPoolCapacity = capacity;
    }
}
