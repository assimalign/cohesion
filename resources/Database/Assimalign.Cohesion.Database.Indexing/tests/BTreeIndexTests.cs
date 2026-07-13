using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

/// <summary>
/// Tests for the B+Tree index (#851): point and range scans, splits at volume,
/// MVCC visibility, unique enforcement through the lock manager, tombstone deletes,
/// property-based key ordering, and crash consistency mid-split.
/// </summary>
public class BTreeIndexTests
{
    private static async Task<(IndexTestHarness Harness, IIndex Index)> CreateIndexAsync(bool unique = false)
    {
        var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(
            setup, objectId: 1, new IndexDefinition("ix_test", IndexKind.BTree, unique));
        await harness.CommitAsync(setup);
        return (harness, index);
    }

    private static async Task<List<(long Key, ulong Reference)>> ScanAsync(IIndex index, ITransactionContext context, IndexKeyRange range, bool reverse = false)
    {
        var results = new List<(long, ulong)>();
        await using var cursor = index.OpenCursor(context, range, reverse);

        while (await cursor.MoveNextAsync())
        {
            long key = DecodeInt64(cursor.CurrentKey);
            results.Add((key, cursor.CurrentEntryReference));
        }

        return results;
    }

    private static long DecodeInt64(IndexKey key)
    {
        ulong folded = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(key.Encoded.Span);
        return (long)(folded ^ 0x8000_0000_0000_0000UL);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - BTree: point lookup returns the inserted entry")]
    public async Task Insert_ThenPointLookup_ShouldReturnEntry()
    {
        // Arrange
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        var transaction = await harness.BeginAsync();
        await index.InsertAsync(transaction, IndexKey.FromInt64(42), 4242);
        await harness.CommitAsync(transaction);

        // Act
        var reader = await harness.BeginAsync();
        var results = await ScanAsync(reader, index, IndexKey.FromInt64(42));

        // Assert
        results.ShouldBe(new[] { (42L, 4242UL) });
    }

    private static Task<List<(long Key, ulong Reference)>> ScanAsync(ITransactionContext context, IIndex index, IndexKey exact)
        => ScanAsync(index, context, new IndexKeyRange(exact, exact, IsStartInclusive: true, IsEndInclusive: true));

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - BTree: thousands of inserts split pages and stay ordered")]
    public async Task Insert_ManyEntries_ShouldSplitAndPreserveOrder()
    {
        // Arrange: enough sequential entries to force leaf splits and a root split.
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        const int entryCount = 2000;
        var transaction = await harness.BeginAsync();

        for (long i = 0; i < entryCount; i++)
        {
            await index.InsertAsync(transaction, IndexKey.FromInt64(i), (ulong)(i * 10));
        }

        await harness.CommitAsync(transaction);

        // Act
        var reader = await harness.BeginAsync();
        var results = await ScanAsync(index, reader, IndexKeyRange.All);

        // Assert
        results.Count.ShouldBe(entryCount);
        results.Select(x => x.Key).ShouldBe(Enumerable.Range(0, entryCount).Select(i => (long)i));
        results[1234].Reference.ShouldBe(12340UL);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - BTree: range and reverse scans respect bounds and inclusivity")]
    public async Task OpenCursor_RangeAndReverse_ShouldRespectBounds()
    {
        // Arrange
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        var transaction = await harness.BeginAsync();
        for (long i = 0; i < 100; i += 10)
        {
            await index.InsertAsync(transaction, IndexKey.FromInt64(i), (ulong)i);
        }
        await harness.CommitAsync(transaction);

        var reader = await harness.BeginAsync();

        // Act: [20, 60) exclusive end, then the same range descending.
        var range = new IndexKeyRange(IndexKey.FromInt64(20), IndexKey.FromInt64(60), IsStartInclusive: true, IsEndInclusive: false);
        var forward = await ScanAsync(index, reader, range);
        var backward = await ScanAsync(index, reader, range, reverse: true);

        // Assert
        forward.Select(x => x.Key).ShouldBe(new[] { 20L, 30L, 40L, 50L });
        backward.Select(x => x.Key).ShouldBe(new[] { 50L, 40L, 30L, 20L });

        // Exclusive start skips the boundary key.
        var exclusiveStart = new IndexKeyRange(IndexKey.FromInt64(20), IndexKey.FromInt64(60), IsStartInclusive: false, IsEndInclusive: true);
        (await ScanAsync(index, reader, exclusiveStart)).Select(x => x.Key).ShouldBe(new[] { 30L, 40L, 50L, 60L });
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - BTree: random insertion order yields sorted scans (property)")]
    public async Task Insert_RandomOrder_ShouldScanSorted()
    {
        // Arrange: a deterministic shuffle of distinct random values.
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        var random = new Random(20260711);
        var values = new HashSet<long>();
        while (values.Count < 1000)
        {
            values.Add(random.NextInt64(long.MinValue, long.MaxValue));
        }

        var shuffled = values.OrderBy(_ => random.Next()).ToList();

        var transaction = await harness.BeginAsync();
        foreach (long value in shuffled)
        {
            await index.InsertAsync(transaction, IndexKey.FromInt64(value), (ulong)value.GetHashCode());
        }
        await harness.CommitAsync(transaction);

        // Act
        var reader = await harness.BeginAsync();
        var results = await ScanAsync(index, reader, IndexKeyRange.All);

        // Assert: scan order equals numeric order.
        results.Select(x => x.Key).ShouldBe(values.OrderBy(x => x));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - BTree: composite keys from the type system order by significance (property)")]
    public async Task Insert_CompositeKeys_ShouldOrderBySignificance()
    {
        // Arrange: (int32, string) composite keys in random order.
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        var pairs = new List<(int Number, string Text)>();
        var random = new Random(42);
        foreach (int number in new[] { 3, 1, 2 })
        {
            foreach (string text in new[] { "zeta", "alpha", "mid" })
            {
                pairs.Add((number, text));
            }
        }
        pairs = pairs.OrderBy(_ => random.Next()).ToList();

        var transaction = await harness.BeginAsync();
        ulong reference = 0;
        foreach (var (number, text) in pairs)
        {
            var writer = new DatabaseKeyWriter();
            writer.AppendInt32(number).AppendString(text, Collation.Binary);
            await index.InsertAsync(transaction, IndexKey.From(writer), reference++);
        }
        await harness.CommitAsync(transaction);

        // Act
        var reader = await harness.BeginAsync();
        var keys = new List<(int, string)>();
        await using var cursor = index.OpenCursor(reader, IndexKeyRange.All);
        while (await cursor.MoveNextAsync())
        {
            var keyReader = new DatabaseKeyReader(cursor.CurrentKey.Encoded.Span);
            keys.Add((keyReader.ReadInt32(), keyReader.ReadString(out _)));
        }

        // Assert
        keys.ShouldBe(pairs.OrderBy(p => p.Number).ThenBy(p => p.Text, StringComparer.Ordinal).ToList());
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - MVCC: uncommitted entries stay invisible to other snapshots")]
    public async Task Insert_Uncommitted_ShouldBeInvisibleToOthers()
    {
        // Arrange
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        var writer = await harness.BeginAsync();
        await index.InsertAsync(writer, IndexKey.FromInt64(7), 700);

        // Act: another transaction and the writer itself.
        var other = await harness.BeginAsync();
        var otherSees = await ScanAsync(index, other, IndexKeyRange.All);
        var writerSees = await ScanAsync(index, writer, IndexKeyRange.All);

        // Assert
        otherSees.ShouldBeEmpty();
        writerSees.Count.ShouldBe(1);

        // After commit a fresh snapshot sees it; the old one still does not.
        await harness.CommitAsync(writer);
        (await ScanAsync(index, other, IndexKeyRange.All)).ShouldBeEmpty();
        var newcomer = await harness.BeginAsync();
        (await ScanAsync(index, newcomer, IndexKeyRange.All)).Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - MVCC: tombstoned entries disappear for new snapshots only")]
    public async Task Delete_Tombstone_ShouldRespectSnapshots()
    {
        // Arrange
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        var setup = await harness.BeginAsync();
        await index.InsertAsync(setup, IndexKey.FromInt64(5), 500);
        await harness.CommitAsync(setup);

        var oldReader = await harness.BeginAsync(); // snapshot before the delete

        var deleter = await harness.BeginAsync();
        await index.DeleteAsync(deleter, IndexKey.FromInt64(5), 500);
        await harness.CommitAsync(deleter);

        // Act / Assert: the old snapshot still sees the entry; a new one does not.
        (await ScanAsync(index, oldReader, IndexKeyRange.All)).Count.ShouldBe(1);
        var newReader = await harness.BeginAsync();
        (await ScanAsync(index, newReader, IndexKeyRange.All)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Unique: duplicate visible key is rejected; delete frees it")]
    public async Task InsertUnique_DuplicateKey_ShouldThrowUntilDeleted()
    {
        // Arrange
        var (harness, index) = await CreateIndexAsync(unique: true);
        await using var harnessLifetime = harness;

        var first = await harness.BeginAsync();
        await index.InsertAsync(first, IndexKey.FromInt64(1), 100);
        await harness.CommitAsync(first);

        // Act / Assert: a duplicate is rejected.
        var second = await harness.BeginAsync();
        await Should.ThrowAsync<IndexUniqueViolationException>(
            async () => await index.InsertAsync(second, IndexKey.FromInt64(1), 200));
        await harness.RollbackAsync(second);

        // Delete the key, then re-insert successfully.
        var third = await harness.BeginAsync();
        await index.DeleteAsync(third, IndexKey.FromInt64(1), 100);
        await index.InsertAsync(third, IndexKey.FromInt64(1), 300);
        await harness.CommitAsync(third);

        var reader = await harness.BeginAsync();
        (await ScanAsync(index, reader, IndexKeyRange.All)).ShouldBe(new[] { (1L, 300UL) });
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Unique: concurrent writers serialize through the lock manager")]
    public async Task InsertUnique_ConcurrentWriters_ShouldSerializeThroughLockManager()
    {
        // Arrange: the first writer holds the key lock (uncommitted).
        var (harness, index) = await CreateIndexAsync(unique: true);
        await using var harnessLifetime = harness;

        var first = await harness.BeginAsync();
        await index.InsertAsync(first, IndexKey.FromInt64(9), 900);

        // Act: the second writer blocks on the key lock rather than racing.
        var second = await harness.BeginAsync();
        var blocked = index.InsertAsync(second, IndexKey.FromInt64(9), 901).AsTask();
        await Task.Delay(100);
        blocked.IsCompleted.ShouldBeFalse();

        // Commit the first: the second acquires the lock, then sees the committed
        // duplicate and fails cleanly.
        await harness.CommitAsync(first);

        await Should.ThrowAsync<IndexUniqueViolationException>(async () => await blocked.WaitAsync(TimeSpan.FromSeconds(5)));
        await harness.RollbackAsync(second);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Recovery: crash mid-split reverts to a consistent tree")]
    public async Task Recovery_CrashMidSplit_ShouldRevertToConsistentTree()
    {
        // Arrange: committed baseline entries, then an uncommitted burst that forces
        // splits, stolen to disk, then a crash.
        var data = new CrashSimulationStream(writeThrough: true);
        var journal = new CrashSimulationStream(writeThrough: false);
        var harness = new IndexTestHarness(data, journal);

        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("ix_crash"));
        for (long i = 0; i < 100; i++)
        {
            await index.InsertAsync(setup, IndexKey.FromInt64(i), (ulong)i);
        }
        await harness.CommitAsync(setup);

        var registrations = ((IIndexRegistry)harness.IndexManager).ExportRegistrations();

        // The doomed transaction: enough inserts to split leaves (and likely the root).
        var doomed = await harness.BeginAsync();
        for (long i = 1000; i < 1600; i++)
        {
            await index.InsertAsync(doomed, IndexKey.FromInt64(i), (ulong)i);
        }

        harness.Storage.PageManager.FlushAll(); // steal everything mid-transaction

        byte[] crashedData = data.CaptureDurable();
        byte[] crashedJournal = journal.CaptureDurable();

        // Act: reopen from the crash and re-attach from the catalog's registrations.
        await using var recovered = IndexTestHarness.Reopen(crashedData, crashedJournal, registrations);
        recovered.IndexManager.TryGetIndex(1, "ix_crash", out var recoveredIndex).ShouldBeTrue();

        var reader = await recovered.BeginAsync();
        var results = await ScanAsync(recoveredIndex, reader, IndexKeyRange.All);

        // Assert: exactly the committed baseline, in order — the mid-split state is gone.
        results.Count.ShouldBe(100);
        results.Select(x => x.Key).ShouldBe(Enumerable.Range(0, 100).Select(i => (long)i));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Recovery: committed splits survive a restart")]
    public async Task Recovery_CommittedSplits_ShouldSurviveRestart()
    {
        // Arrange
        var data = new CrashSimulationStream(writeThrough: false);
        var journal = new CrashSimulationStream(writeThrough: false);
        var harness = new IndexTestHarness(data, journal);

        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("ix_restart"));
        for (long i = 0; i < 800; i++)
        {
            await index.InsertAsync(setup, IndexKey.FromInt64(i), (ulong)i);
        }
        await harness.CommitAsync(setup);

        var registrations = ((IIndexRegistry)harness.IndexManager).ExportRegistrations();

        // Crash WITHOUT any page flush: only the WAL is durable.
        byte[] crashedData = data.CaptureDurable();
        byte[] crashedJournal = journal.CaptureDurable();

        // Act
        await using var recovered = IndexTestHarness.Reopen(crashedData, crashedJournal, registrations);
        recovered.IndexManager.TryGetIndex(1, "ix_restart", out var recoveredIndex).ShouldBeTrue();

        var reader = await recovered.BeginAsync();
        var results = await ScanAsync(recoveredIndex, reader, IndexKeyRange.All);

        // Assert: redo rebuilt every split page from the journal.
        results.Count.ShouldBe(800);
        results.Select(x => x.Key).ShouldBe(Enumerable.Range(0, 800).Select(i => (long)i));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Manager: duplicate names rejected, drop removes, registry exports")]
    public async Task IndexManager_Lifecycle_ShouldEnforceDirectoryRules()
    {
        // Arrange
        var (harness, _) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        var transaction = await harness.BeginAsync();

        // Act / Assert
        await Should.ThrowAsync<IndexException>(async () =>
            await harness.IndexManager.CreateIndexAsync(transaction, 1, new IndexDefinition("ix_test")));

        harness.IndexManager.TryGetIndex(1, "ix_test", out var found).ShouldBeTrue();
        found.Name.ShouldBe("ix_test");
        harness.IndexManager.GetIndexes(1).Count.ShouldBe(1);

        ((IIndexRegistry)harness.IndexManager).ExportRegistrations().Count.ShouldBe(1);

        await harness.IndexManager.DropIndexAsync(transaction, 1, "ix_test");
        harness.IndexManager.TryGetIndex(1, "ix_test", out var afterDrop).ShouldBeFalse();
        await Should.ThrowAsync<IndexException>(async () =>
            await harness.IndexManager.DropIndexAsync(transaction, 1, "ix_test"));

        await harness.RollbackAsync(transaction);
    }
}
