using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

using static KeyValueTestHarness;

/// <summary>
/// Entry records that pass their page's checksum and owner check and still do not decode (#1362).
/// Every command reads an entry record through a primary-index seek, with
/// <c>Storage.TryReadRecord</c> and the key space's owner id, so a record it finds in a live slot is
/// a key-space entry record, and one that does not decode is damaged: the command fails with
/// <see cref="StorageCorruptionException"/> instead of reading the key as missing. Each damage here
/// is written through the storage API in its own committed bracket, and the page is then evicted,
/// so the command reads it back from the data file and its checksum verifies.
/// </summary>
public sealed class KeyValueRecordDecodeIntegrityTests
{
    /// <summary>How a test damages an entry record.</summary>
    public enum RecordDamage
    {
        /// <summary>The key component's type tag is no type at all.</summary>
        InvalidKeyTag,

        /// <summary>A byte follows the value component.</summary>
        TrailingBytes,

        /// <summary>The record ends inside the value component.</summary>
        Truncated,

        /// <summary>The record is shorter than its version stamps.</summary>
        ShorterThanStamps,
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Record decode: a Get over an entry record that does not decode fails with StorageCorruptionException (#1362)")]
    [InlineData(RecordDamage.InvalidKeyTag)]
    [InlineData(RecordDamage.TrailingBytes)]
    [InlineData(RecordDamage.Truncated)]
    [InlineData(RecordDamage.ShorterThanStamps)]
    public async Task Get_EntryRecordThatDoesNotDecode_ShouldFailWithStorageCorruption(RecordDamage damage)
    {
        // Arrange: three keys on one page; the middle one's record damaged.
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync(DatabaseName, TestTimeout.Token());
        await using var session = await database.CreateSessionAsync();
        await PutAsync(database, session, "alpha", "bravo", "charlie");
        var damaged = EntryLocation(database, "bravo");
        Damage(database, damaged, damage);

        // Act
        var failure = await Should.ThrowAsync<StorageCorruptionException>(
            () => database.GetAsync(session, Bytes("bravo"), TestTimeout.Token()).AsTask());
        var neighbour = await database.GetAsync(session, Bytes("alpha"), TestTimeout.Token());

        // Assert: the failure names the record; the page itself reads, so the key beside it does.
        failure.PageId.ShouldBe(damaged.PageId);
        failure.Message.ShouldStartWith(
            $"The key-value entry record in slot {damaged.SlotIndex} of page {(long)damaged.PageId} does not decode: ", Case.Sensitive);
        Text(neighbour.ShouldNotBeNull().Value).ShouldBe("value-alpha");
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Record decode: a scan over an entry record that does not decode fails with StorageCorruptionException (#1362)")]
    [InlineData(RecordDamage.InvalidKeyTag)]
    [InlineData(RecordDamage.TrailingBytes)]
    [InlineData(RecordDamage.Truncated)]
    [InlineData(RecordDamage.ShorterThanStamps)]
    public async Task Scan_EntryRecordThatDoesNotDecode_ShouldFailWithStorageCorruption(RecordDamage damage)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync(DatabaseName, TestTimeout.Token());
        await using var session = await database.CreateSessionAsync();
        await PutAsync(database, session, "alpha", "bravo", "charlie");
        var damaged = EntryLocation(database, "bravo");
        Damage(database, damaged, damage);

        // Act
        var failure = await Should.ThrowAsync<StorageCorruptionException>(() => ScanAsync(database, session));
        var beforeTheDamage = await ScanAsync(database, session, new KeyValueScanOptions { End = Bytes("bravo") });

        // Assert: a range that stops short of the damaged key still reads.
        failure.PageId.ShouldBe(damaged.PageId);
        failure.Message.ShouldContain($"slot {damaged.SlotIndex} of page {(long)damaged.PageId} does not decode", Case.Sensitive);
        beforeTheDamage.ShouldBe(["alpha"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Record decode: a scan skips an entry whose record was reclaimed beneath it, not reported as corruption (#1362)")]
    public async Task Scan_EntryRecordReclaimedBeneathIndex_ShouldSkipTheEntry()
    {
        // Arrange: the middle key's record deleted beneath its index entry, the state a purge or
        // an undo leaves under an entry a reader already holds.
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync(DatabaseName, TestTimeout.Token());
        await using var session = await database.CreateSessionAsync();
        await PutAsync(database, session, "alpha", "bravo", "charlie");
        var reclaimed = EntryLocation(database, "bravo");
        using (var bracket = database.DataStorage.BeginTransaction())
        {
            database.DataStorage.DeleteEntry(bracket, reclaimed.PageId, reclaimed.SlotIndex);
            bracket.Commit();
        }

        // Act
        var keys = await ScanAsync(database, session);

        // Assert
        keys.ShouldBe(["alpha", "charlie"]);
    }

    private static KeyValueDatabaseEngine CreateEngine()
        => KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions
        {
            EngineName = "kv-record-decode",
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });

    private static async Task PutAsync(KeyValueDatabase database, KeyValueDatabaseSession session, params string[] keys)
    {
        foreach (string key in keys)
        {
            await database.PutAsync(session, Bytes(key), Bytes("value-" + key), cancellationToken: TestTimeout.Token());
        }
    }

    private static async Task<List<string>> ScanAsync(KeyValueDatabase database, KeyValueDatabaseSession session, KeyValueScanOptions? options = null)
    {
        var keys = new List<string>();
        await foreach (var entry in database.ScanAsync(session, options, TestTimeout.Token()))
        {
            keys.Add(Text(entry.Key));
        }

        return keys;
    }

    private static (PageId PageId, int SlotIndex) EntryLocation(KeyValueDatabase database, string key)
    {
        using var iterator = database.DataStorage.GetUnitIterator(KeyValueOperationExecutor.KeySpaceObjectId);
        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            KeyValueRecordCodec.Decode(unit.Data.Span, out byte[] stored, out _, out _, out _);
            if (Text(stored) == key)
            {
                return (unit.PageId, unit.SlotIndex);
            }
        }

        throw new InvalidOperationException($"The record of key '{key}' was not found.");
    }

    /// <summary>
    /// Rewrites an entry record damaged, through the storage API in a committed bracket, then
    /// evicts its page, which writes it back with its checksum recomputed: the next read verifies
    /// the page and finds the damaged record in a live slot.
    /// </summary>
    private static void Damage(KeyValueDatabase database, (PageId PageId, int SlotIndex) location, RecordDamage damage)
    {
        var storage = database.DataStorage;
        byte[] record = storage.ReadEntry(location.PageId, location.SlotIndex).ToArray();
        const int keyTag = KeyValueRecordCodec.StampHeaderSize;
        record[keyTag].ShouldBe((byte)DatabaseType.Binary);

        byte[] damaged = damage switch
        {
            RecordDamage.InvalidKeyTag => Patched(record, keyTag, 0xEE),
            RecordDamage.TrailingBytes => [.. record, 0x00],
            RecordDamage.Truncated => record[..^1],
            RecordDamage.ShorterThanStamps => record[..8],
            _ => throw new ArgumentOutOfRangeException(nameof(damage)),
        };

        using (var bracket = storage.BeginTransaction())
        {
            storage.UpdateEntry(bracket, location.PageId, location.SlotIndex, damaged);
            bracket.Commit();
        }

        Evict(database, location.PageId);
    }

    private static byte[] Patched(byte[] record, int offset, byte value)
    {
        byte[] patched = record.ToArray();
        patched[offset] = value;
        return patched;
    }

    /// <summary>
    /// Evicts a page from the data storage's buffer pool: the pool shrinks to one frame, which
    /// another page then takes, and grows back. A dirty page is written back first.
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
