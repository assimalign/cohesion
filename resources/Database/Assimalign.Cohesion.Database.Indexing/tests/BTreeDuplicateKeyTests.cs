using System;
using System.Buffers.Binary;
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
/// Regression suite for #1159: keys whose duplicates span one or more leaf splits.
/// Every lookup path — equality and range seeks, tombstone deletes, the physical
/// undo pair, and the unique check — must find exactly the entries a full scan
/// finds, and the tree must stay ordered while keys keep arriving around a long
/// run of equal keys. The randomized tests check seeks against both the scan and an
/// independent model across inserts, deletes, rollbacks, logical undo, build-path
/// history versions, and the open-time purge.
/// </summary>
public class BTreeDuplicateKeyTests
{
    private static async Task<(IndexTestHarness Harness, IIndex Index)> CreateIndexAsync(bool unique = false)
    {
        var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(
            setup, objectId: 1, new IndexDefinition("ix_duplicates", IndexKind.BTree, unique));
        await harness.CommitAsync(setup);
        return (harness, index);
    }

    private static async Task<List<(byte[] Key, ulong Reference)>> ReadAsync(
        IIndex index, TransactionContext reader, IndexKeyRange range, bool reverse = false)
    {
        var results = new List<(byte[] Key, ulong Reference)>();
        await using var cursor = index.OpenCursor(reader, range, reverse);

        while (await cursor.MoveNextAsync())
        {
            results.Add((cursor.CurrentKey.Encoded.ToArray(), cursor.CurrentEntryReference));
        }

        return results;
    }

    private static IndexKeyRange Exactly(IndexKey key) => new(key, key, IsStartInclusive: true, IsEndInclusive: true);

    /// <summary>
    /// The SQL engine's encoding of an INT column value (type tag + order-preserving
    /// big-endian body), so the index-level reproduction uses the reported workload's
    /// exact key bytes.
    /// </summary>
    private static IndexKey Int32Key(int value) => IndexKey.From(new DatabaseKeyWriter().AppendInt32(value));

    /// <summary>
    /// An order-preserving key for <paramref name="value"/> followed by
    /// <paramref name="padding"/> filler bytes: equal values give equal keys, and the
    /// padding controls how many entries fit on a page (so how often nodes split).
    /// </summary>
    private static IndexKey PaddedKey(long value, int padding)
    {
        var bytes = new byte[sizeof(long) + padding];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, (ulong)value ^ 0x8000_0000_0000_0000UL);
        bytes.AsSpan(sizeof(long)).Fill(0x5A);
        return new IndexKey(bytes);
    }

    private static bool InRange(byte[] key, IndexKeyRange range)
    {
        if (range.Start is { } start)
        {
            int comparison = key.AsSpan().SequenceCompareTo(start.Encoded.Span);
            if (comparison < 0 || (comparison == 0 && !range.IsStartInclusive))
            {
                return false;
            }
        }

        if (range.End is { } end)
        {
            int comparison = key.AsSpan().SequenceCompareTo(end.Encoded.Span);
            if (comparison > 0 || (comparison == 0 && !range.IsEndInclusive))
            {
                return false;
            }
        }

        return true;
    }

    private static void ShouldBeOrdered(List<(byte[] Key, ulong Reference)> scan)
    {
        for (int i = 1; i < scan.Count; i++)
        {
            if (scan[i - 1].Key.AsSpan().SequenceCompareTo(scan[i].Key) > 0)
            {
                throw new ShouldAssertException(
                    $"Scan order broken at position {i}: {Convert.ToHexString(scan[i - 1].Key)} precedes {Convert.ToHexString(scan[i].Key)}.");
            }
        }
    }

    /// <summary>
    /// Asserts that a seek over <paramref name="range"/> returns exactly the entries
    /// the full scan holds in that range, in the same order — forward and reversed.
    /// </summary>
    private static async Task SeekShouldMatchScanAsync(
        IIndex index, TransactionContext reader, List<(byte[] Key, ulong Reference)> scan, IndexKeyRange range)
    {
        var expected = scan.Where(entry => InRange(entry.Key, range)).ToList();

        var forward = await ReadAsync(index, reader, range);
        Describe(forward).ShouldBe(Describe(expected), $"seek {Describe(range)} diverged from the scan");

        var backward = await ReadAsync(index, reader, range, reverse: true);
        expected.Reverse();
        Describe(backward).ShouldBe(Describe(expected), $"reverse seek {Describe(range)} diverged from the scan");
    }

    private static List<string> Describe(List<(byte[] Key, ulong Reference)> entries)
        => entries.Select(entry => $"{Convert.ToHexString(entry.Key)}:{entry.Reference}").ToList();

    private static string Describe(IndexKeyRange range)
        => $"{(range.IsStartInclusive ? '[' : '(')}{range.Start?.ToString() ?? "-inf"}, {range.End?.ToString() ?? "+inf"}{(range.IsEndInclusive ? ']' : ')')}";

    /// <summary>
    /// Advances the committed horizon so explicit stamps at the returned sequence read
    /// as committed history.
    /// </summary>
    private static async Task<TransactionSequence> CommittedSequenceAsync(IndexTestHarness harness)
    {
        var context = await harness.BeginAsync();
        await harness.CommitAsync(context);
        return context.Sequence;
    }

    // ── The reported workload ───────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Duplicates: the reported qty = id % 997 workload seeks every duplicate (#1159)")]
    public async Task Seek_ModuloWorkload_ShouldReturnEveryDuplicateOfEveryKey()
    {
        // Arrange: the issue's workload at the index level — 12,000 rows keyed by
        // id % 997 in the SQL engine's INT encoding, so 36 keys carry 13 entries and
        // the rest 12, and many runs straddle a leaf split.
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        const int rows = 12_000;
        const int modulus = 997;

        for (int batch = 0; batch < rows; batch += 1_000)
        {
            var writer = await harness.BeginAsync();
            for (int id = batch; id < batch + 1_000; id++)
            {
                await index.InsertAsync(writer, Int32Key(id % modulus), (ulong)id);
            }
            await harness.CommitAsync(writer);
        }

        // Act
        var reader = await harness.BeginAsync();
        var scan = await ReadAsync(index, reader, IndexKeyRange.All);

        var mismatches = new List<string>();
        for (int value = 0; value < modulus; value++)
        {
            var expected = new List<ulong>();
            for (int id = value; id < rows; id += modulus)
            {
                expected.Add((ulong)id);
            }

            var actual = (await ReadAsync(index, reader, Exactly(Int32Key(value)))).Select(entry => entry.Reference).Order().ToList();
            if (!actual.SequenceEqual(expected))
            {
                mismatches.Add($"qty = {value}: {actual.Count} of {expected.Count}");
            }
        }

        // Assert
        scan.Count.ShouldBe(rows);
        ShouldBeOrdered(scan);
        mismatches.ShouldBeEmpty();
    }

    // ── Long duplicate runs ─────────────────────────────────────────────

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Duplicates: a run spanning many leaf splits is found by equality and range seeks")]
    [InlineData(150)]
    [InlineData(1_000)]
    [InlineData(6_000)]
    public async Task Seek_DuplicateRunAcrossLeafSplits_ShouldReturnEveryDuplicate(int duplicates)
    {
        // Arrange: one hot key interleaved with distinct neighbours on both sides, so
        // the run's leaves also hold (and split around) other keys.
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        const long hot = 500;
        var model = new List<(long Value, ulong Reference)>();
        ulong reference = 0;

        var writer = await harness.BeginAsync();
        for (int i = 0; i < duplicates; i++)
        {
            await index.InsertAsync(writer, IndexKey.FromInt64(hot), reference);
            model.Add((hot, reference++));

            if (i % 4 == 0)
            {
                long below = hot - 1 - (i / 4 % 100);
                long above = hot + 1 + (i / 4 % 100);
                await index.InsertAsync(writer, IndexKey.FromInt64(below), reference);
                model.Add((below, reference++));
                await index.InsertAsync(writer, IndexKey.FromInt64(above), reference);
                model.Add((above, reference++));
            }
        }
        await harness.CommitAsync(writer);

        // Act
        var reader = await harness.BeginAsync();
        var scan = await ReadAsync(index, reader, IndexKeyRange.All);
        var hotEntries = await ReadAsync(index, reader, Exactly(IndexKey.FromInt64(hot)));

        // Assert: the seek finds the whole run, and every range shape agrees with the scan.
        scan.Count.ShouldBe(model.Count);
        ShouldBeOrdered(scan);
        hotEntries.Select(entry => entry.Reference).Order()
            .ShouldBe(model.Where(entry => entry.Value == hot).Select(entry => entry.Reference).Order());

        IndexKey Key(long value) => IndexKey.FromInt64(value);
        var ranges = new[]
        {
            new IndexKeyRange(Key(hot - 1), Key(hot + 1), true, true),
            new IndexKeyRange(Key(hot - 1), Key(hot + 1), false, false),
            new IndexKeyRange(Key(hot), Key(hot + 1), true, false),
            new IndexKeyRange(Key(hot), Key(hot + 50), false, true),
            new IndexKeyRange(Key(hot - 50), Key(hot), true, false),
            new IndexKeyRange(Key(hot - 50), Key(hot), false, true),
            new IndexKeyRange(Key(hot), null, true, false),
            new IndexKeyRange(null, Key(hot), true, true),
            new IndexKeyRange(Key(hot), Key(hot), false, true),
        };

        foreach (var range in ranges)
        {
            await SeekShouldMatchScanAsync(index, reader, scan, range);
        }

        for (long value = hot - 101; value <= hot + 101; value++)
        {
            await SeekShouldMatchScanAsync(index, reader, scan, Exactly(Key(value)));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Duplicates: keys arriving around a split run keep the leaf chain ordered")]
    public async Task Insert_AroundSplitDuplicateRun_ShouldKeepLeafChainOrdered()
    {
        // Arrange: a run long enough to split many times under one parent, so the
        // parent holds several equal separators; then neighbours on both sides and
        // more duplicates. Each split must attach its new leaf directly after the leaf
        // it split — positioning by separator value alone cannot tell equal
        // separators apart.
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        const long hot = 10_000;
        var model = new Dictionary<long, int>();
        ulong reference = 0;

        async Task InsertAsync(TransactionContext transaction, long value)
        {
            await index.InsertAsync(transaction, IndexKey.FromInt64(value), reference++);
            model[value] = model.GetValueOrDefault(value) + 1;
        }

        var writer = await harness.BeginAsync();
        for (int i = 0; i < 3_000; i++)
        {
            await InsertAsync(writer, hot);
        }
        for (int i = 1; i <= 400; i++)
        {
            await InsertAsync(writer, hot + i);
            await InsertAsync(writer, hot - i);
        }
        for (int i = 0; i < 500; i++)
        {
            await InsertAsync(writer, hot);
            await InsertAsync(writer, hot + 1 + (i % 7));
        }
        await harness.CommitAsync(writer);

        // Act
        var reader = await harness.BeginAsync();
        var scan = await ReadAsync(index, reader, IndexKeyRange.All);

        // Assert
        scan.Count.ShouldBe(model.Values.Sum());
        ShouldBeOrdered(scan);

        foreach (var (value, count) in model)
        {
            (await ReadAsync(index, reader, Exactly(IndexKey.FromInt64(value)))).Count.ShouldBe(count, $"key {value}");
        }

        await SeekShouldMatchScanAsync(index, reader, scan,
            new IndexKeyRange(IndexKey.FromInt64(hot - 3), IndexKey.FromInt64(hot + 3), true, true));
    }

    // ── Write paths that find an existing entry ─────────────────────────

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Duplicates: deletes tombstone the matching entry anywhere in a split run")]
    public async Task Delete_DuplicatesAcrossLeafSplits_ShouldTombstoneEachMatchingEntry()
    {
        // Arrange: a 1,500-entry run with neighbours, committed.
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        const long hot = 77;
        const int duplicates = 1_500;
        var setup = await harness.BeginAsync();
        for (int i = 0; i < duplicates; i++)
        {
            await index.InsertAsync(setup, IndexKey.FromInt64(hot), (ulong)i);
            if (i % 5 == 0)
            {
                await index.InsertAsync(setup, IndexKey.FromInt64(hot + 1), 100_000UL + (ulong)i);
            }
        }
        await harness.CommitAsync(setup);

        var before = await harness.BeginAsync(); // snapshot taken before the deletes

        // Act: tombstone every third entry, in shuffled order, then repeat one delete
        // (an already-tombstoned mapping must be a no-op).
        var random = new Random(1159);
        var doomed = Enumerable.Range(0, duplicates).Where(i => i % 3 == 0).OrderBy(_ => random.Next()).ToList();
        var deleter = await harness.BeginAsync();
        foreach (int i in doomed)
        {
            await index.DeleteAsync(deleter, IndexKey.FromInt64(hot), (ulong)i);
        }
        await index.DeleteAsync(deleter, IndexKey.FromInt64(hot), (ulong)doomed[0]);
        await harness.CommitAsync(deleter);

        // Assert: new snapshots see exactly the survivors; the old one still sees all.
        var after = await harness.BeginAsync();
        (await ReadAsync(index, after, Exactly(IndexKey.FromInt64(hot)))).Select(entry => entry.Reference).Order()
            .ShouldBe(Enumerable.Range(0, duplicates).Where(i => i % 3 != 0).Select(i => (ulong)i));
        (await ReadAsync(index, before, Exactly(IndexKey.FromInt64(hot)))).Count.ShouldBe(duplicates);
        (await ReadAsync(index, after, Exactly(IndexKey.FromInt64(hot + 1)))).Count.ShouldBe(duplicates / 5);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Duplicates: erase and clear-deleter find their entry anywhere in a split run")]
    public async Task UndoPair_DuplicatesAcrossLeafSplits_ShouldRestoreTheCommittedRun()
    {
        // Arrange: a committed run built through the stamp-preserving path, then a
        // transaction that tombstones half of it and adds 700 more duplicates.
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        const long hot = 4_096;
        const int duplicates = 1_200;
        var committed = await CommittedSequenceAsync(harness);

        using (var build = harness.Storage.BeginTransaction())
        {
            for (int i = 0; i < duplicates; i++)
            {
                await index.InsertVersionAsync(build, IndexKey.FromInt64(hot), (ulong)i, committed, TransactionSequence.None);
            }
            build.Commit();
        }

        var aborting = await harness.BeginAsync();
        for (int i = 0; i < duplicates; i += 2)
        {
            await index.DeleteAsync(aborting, IndexKey.FromInt64(hot), (ulong)i);
        }
        for (int i = 0; i < 700; i++)
        {
            await index.InsertAsync(aborting, IndexKey.FromInt64(hot), 50_000UL + (ulong)i);
        }

        // Act: the engine's ROLLBACK — the statement brackets stay durable and the
        // logical undo erases the inserts and clears the tombstones. A mismatched
        // writer must not erase a committed entry.
        await harness.LogicalRollbackAsync(aborting, async undo =>
        {
            for (int i = 0; i < 700; i++)
            {
                await index.EraseAsync(undo, IndexKey.FromInt64(hot), 50_000UL + (ulong)i, aborting.Sequence);
            }
            for (int i = 0; i < duplicates; i += 2)
            {
                await index.ClearDeleterAsync(undo, IndexKey.FromInt64(hot), (ulong)i, aborting.Sequence);
            }
            await index.EraseAsync(undo, IndexKey.FromInt64(hot), 1UL, aborting.Sequence);
        });

        // Assert: exactly the committed run is visible again.
        var reader = await harness.BeginAsync();
        (await ReadAsync(index, reader, Exactly(IndexKey.FromInt64(hot)))).Select(entry => entry.Reference).Order()
            .ShouldBe(Enumerable.Range(0, duplicates).Select(i => (ulong)i));
        ShouldBeOrdered(await ReadAsync(index, reader, IndexKeyRange.All));
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Duplicates: the unique check sees a live entry split away from its dead versions")]
    [InlineData(150)]
    [InlineData(400)]
    [InlineData(1_000)]
    public async Task InsertUnique_LiveVersionBehindDeadVersions_ShouldReject(int deadVersions)
    {
        // Arrange: a unique key with a long history of dead versions (an UPDATE-heavy
        // row: every update tombstones one entry and adds another), its one live
        // version, then distinct neighbours that fill the leaf and split it inside
        // the run — leaving the live version on a leaf to the left of the separator.
        var (harness, index) = await CreateIndexAsync(unique: true);
        await using var harnessLifetime = harness;

        const int hot = 1_000;
        const ulong live = 999_999;
        var writer = await CommittedSequenceAsync(harness);
        var deleter = await CommittedSequenceAsync(harness);

        using (var build = harness.Storage.BeginTransaction())
        {
            for (int i = 0; i < deadVersions; i++)
            {
                await index.InsertVersionAsync(build, Int32Key(hot), (ulong)i, writer, deleter);
            }
            await index.InsertVersionAsync(build, Int32Key(hot), live, writer, TransactionSequence.None);
            for (int i = 1; i <= 400; i++)
            {
                await index.InsertVersionAsync(build, Int32Key(hot + i), 500_000UL + (ulong)i, writer, TransactionSequence.None);
            }
            build.Commit();
        }

        // Act / Assert: a second live entry for the key is rejected.
        var duplicate = await harness.BeginAsync();
        await Should.ThrowAsync<IndexUniqueViolationException>(async () => await index.InsertAsync(duplicate, Int32Key(hot), 1));
        await harness.RollbackAsync(duplicate);

        var reader = await harness.BeginAsync();
        (await ReadAsync(index, reader, Exactly(Int32Key(hot)))).Select(entry => entry.Reference).ShouldBe(new[] { live });

        // The update shape: tombstone the live version, insert its successor.
        var update = await harness.BeginAsync();
        await index.DeleteAsync(update, Int32Key(hot), live);
        await index.InsertAsync(update, Int32Key(hot), 2);
        await harness.CommitAsync(update);

        var afterUpdate = await harness.BeginAsync();
        (await ReadAsync(index, afterUpdate, Exactly(Int32Key(hot)))).Select(entry => entry.Reference).ShouldBe(new[] { 2UL });

        var second = await harness.BeginAsync();
        await Should.ThrowAsync<IndexUniqueViolationException>(async () => await index.InsertAsync(second, Int32Key(hot), 3));
        await harness.RollbackAsync(second);
    }

    // ── Structural edges the split path must survive ────────────────────

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Splits: internal nodes split by bytes, so a maximum-length separator always fits")]
    public async Task Insert_SkewedKeyLengths_ShouldSplitInternalNodesWithRoom()
    {
        // Arrange: thousands of short keys build a wide parent of short separators;
        // then maximum-length keys sorting before all of them split leaves whose
        // separators are maximum length and land in the parent's left part. A split
        // that halves the parent by entry count leaves that part too full to take
        // another maximum-length separator.
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        static IndexKey Short(int value)
        {
            var bytes = new byte[9];
            bytes[0] = 0x01;
            BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(1), value);
            return new IndexKey(bytes);
        }

        static IndexKey Long(int value)
        {
            var bytes = new byte[1024];
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(1), value); // bytes[0] = 0x00 sorts first
            bytes.AsSpan(5).Fill(0x33);
            return new IndexKey(bytes);
        }

        var writer = await harness.BeginAsync();
        for (int i = 0; i < 6_000; i++)
        {
            await index.InsertAsync(writer, Short(i), (ulong)i);
        }
        for (int i = 0; i < 400; i++)
        {
            await index.InsertAsync(writer, Long(i), 100_000UL + (ulong)i);
        }
        await harness.CommitAsync(writer);

        // Act
        var reader = await harness.BeginAsync();
        var scan = await ReadAsync(index, reader, IndexKeyRange.All);

        // Assert
        scan.Count.ShouldBe(6_400);
        ShouldBeOrdered(scan);
        for (int i = 0; i < 400; i += 37)
        {
            (await ReadAsync(index, reader, Exactly(Long(i)))).Select(entry => entry.Reference).ShouldBe(new[] { 100_000UL + (ulong)i });
        }
        for (int i = 0; i < 6_000; i += 191)
        {
            (await ReadAsync(index, reader, Exactly(Short(i)))).Select(entry => entry.Reference).ShouldBe(new[] { (ulong)i });
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Splits: a leaf emptied by undo reclaims its space instead of splitting")]
    public async Task Insert_IntoLeavesEmptiedByUndo_ShouldStayOrdered()
    {
        // Arrange: large keys (seven to a leaf), so many leaves are exactly full. An
        // aborted writer fills a whole key range in random order; its undo erases
        // every entry, leaving leaves with no entries but no free space either.
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        const int padding = 1_000;
        var random = new Random(31);
        var committed = await CommittedSequenceAsync(harness);
        var model = new Dictionary<ulong, long>();

        using (var build = harness.Storage.BeginTransaction())
        {
            for (long value = 0; value < 2_000; value += 50)
            {
                await index.InsertVersionAsync(build, PaddedKey(value, padding), (ulong)value, committed, TransactionSequence.None);
                model[(ulong)value] = value;
            }
            build.Commit();
        }

        var aborting = await harness.BeginAsync();
        var burst = Enumerable.Range(0, 400).Select(i => (long)(i * 5 + 1)).OrderBy(_ => random.Next()).ToList();
        foreach (long value in burst)
        {
            await index.InsertAsync(aborting, PaddedKey(value, padding), 10_000UL + (ulong)value);
        }

        await harness.LogicalRollbackAsync(aborting, async undo =>
        {
            foreach (long value in burst)
            {
                await index.EraseAsync(undo, PaddedKey(value, padding), 10_000UL + (ulong)value, aborting.Sequence);
            }
        });

        // Act: the same key range arrives again, in a different order.
        var writer = await harness.BeginAsync();
        foreach (long value in burst.OrderBy(_ => random.Next()))
        {
            await index.InsertAsync(writer, PaddedKey(value, padding), 20_000UL + (ulong)value);
            model[20_000UL + (ulong)value] = value;
        }
        await harness.CommitAsync(writer);

        // Assert
        var reader = await harness.BeginAsync();
        var scan = await ReadAsync(index, reader, IndexKeyRange.All);
        scan.Count.ShouldBe(model.Count);
        ShouldBeOrdered(scan);
        scan.Select(entry => entry.Reference).Order().ShouldBe(model.Keys.Order());
        foreach (long value in burst.Take(60))
        {
            (await ReadAsync(index, reader, Exactly(PaddedKey(value, padding)))).Select(entry => entry.Reference)
                .ShouldBe(new[] { 20_000UL + (ulong)value });
        }
    }

    // ── Randomized model checks ─────────────────────────────────────────

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Duplicates: randomized workload — every seek equals the scan and the model")]
    [InlineData(1159, 0)]
    [InlineData(20261001, 0)]
    [InlineData(42, 96)]
    [InlineData(7, 400)]
    [InlineData(911, -1)] // variable key lengths: padding derived from the value
    public async Task RandomizedWorkload_SeeksShouldMatchScanAndModel(int seed, int padding)
    {
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        var random = new Random(seed);
        const int domain = 24;
        const long hot = 7;

        IndexKey Key(long value) => PaddedKey(value, padding >= 0 ? padding : (int)((value + 1) * 131 % 900));
        long NextValue() => random.NextDouble() < 0.4 ? hot : random.Next(domain);

        var live = new Dictionary<ulong, long>(); // committed, visible to new snapshots: reference → value
        ulong nextReference = 1;
        var unproven = new TransactionSequence(1_000_000_000); // never assigned: an open-time purge target

        // Live entries carrying an unproven deleter stamp cannot be tombstoned again
        // until the purge clears that stamp (a delete only stamps deleter-free entries).
        var unprovenTombstones = new HashSet<ulong>();

        TransactionContext? oldReader = null;
        Dictionary<ulong, long>? oldModel = null;

        var historyWriter = await CommittedSequenceAsync(harness);
        var historyDeleter = await CommittedSequenceAsync(harness);

        const int rounds = 70;
        for (int round = 0; round < rounds; round++)
        {
            int kind = random.Next(20);

            if (kind < 13)
            {
                // An ordinary transaction: inserts and tombstones, committed or physically rolled back.
                var transaction = await harness.BeginAsync();
                var inserted = new Dictionary<ulong, long>();
                var deleted = new HashSet<ulong>();

                for (int op = random.Next(10, 70); op > 0; op--)
                {
                    var candidates = live.Keys.Concat(inserted.Keys)
                        .Where(reference => !deleted.Contains(reference) && !unprovenTombstones.Contains(reference))
                        .ToList();
                    if (candidates.Count == 0 || random.NextDouble() < 0.62)
                    {
                        long value = NextValue();
                        ulong reference = nextReference++;
                        await index.InsertAsync(transaction, Key(value), reference);
                        inserted[reference] = value;
                    }
                    else
                    {
                        ulong reference = candidates[random.Next(candidates.Count)];
                        long value = inserted.TryGetValue(reference, out long own) ? own : live[reference];
                        await index.DeleteAsync(transaction, Key(value), reference);
                        deleted.Add(reference);
                    }
                }

                if (random.NextDouble() < 0.8)
                {
                    await harness.CommitAsync(transaction);
                    foreach (var (reference, value) in inserted)
                    {
                        live[reference] = value;
                    }
                    foreach (ulong reference in deleted)
                    {
                        live.Remove(reference);
                    }
                }
                else
                {
                    await harness.RollbackAsync(transaction);
                }
            }
            else if (kind < 17)
            {
                // The engine's ROLLBACK after committed statement brackets: the undo pair
                // must find every entry the transaction touched.
                var transaction = await harness.BeginAsync();
                var inserted = new List<(ulong Reference, long Value)>();
                var deleted = new List<(ulong Reference, long Value)>();
                var available = live.Where(entry => !unprovenTombstones.Contains(entry.Key)).ToList();

                for (int op = random.Next(10, 60); op > 0; op--)
                {
                    if (available.Count == 0 || random.NextDouble() < 0.6)
                    {
                        long value = NextValue();
                        ulong reference = nextReference++;
                        await index.InsertAsync(transaction, Key(value), reference);
                        inserted.Add((reference, value));
                    }
                    else
                    {
                        int pick = random.Next(available.Count);
                        var (reference, value) = available[pick];
                        available.RemoveAt(pick);
                        await index.DeleteAsync(transaction, Key(value), reference);
                        deleted.Add((reference, value));
                    }
                }

                await harness.LogicalRollbackAsync(transaction, async undo =>
                {
                    foreach (var (reference, value) in inserted)
                    {
                        await index.EraseAsync(undo, Key(value), reference, transaction.Sequence);
                    }
                    foreach (var (reference, value) in deleted)
                    {
                        await index.ClearDeleterAsync(undo, Key(value), reference, transaction.Sequence);
                    }
                });
            }
            else if (kind < 19)
            {
                // Build-path history: dead versions, live versions, and versions whose
                // writer or deleter is unproven (never committed).
                using var build = harness.Storage.BeginTransaction();
                for (int op = random.Next(20, 80); op > 0; op--)
                {
                    long value = NextValue();
                    ulong reference = nextReference++;
                    switch (random.Next(4))
                    {
                        case 0:
                            await index.InsertVersionAsync(build, Key(value), reference, historyWriter, historyDeleter);
                            break;
                        case 1:
                            await index.InsertVersionAsync(build, Key(value), reference, unproven, TransactionSequence.None);
                            break;
                        default:
                            var deleter = random.Next(3) == 0 ? unproven : TransactionSequence.None;
                            await index.InsertVersionAsync(build, Key(value), reference, historyWriter, deleter);
                            live[reference] = value;
                            oldModel?.TryAdd(reference, value); // history is visible to every snapshot
                            if (deleter != TransactionSequence.None)
                            {
                                unprovenTombstones.Add(reference);
                            }
                            break;
                    }
                }
                build.Commit();
            }
            else
            {
                // The open-time purge: unproven writers' entries vanish, their tombstones clear.
                using var scrub = harness.Storage.BeginTransaction();
                await harness.IndexManager.PurgeWritersAsync(scrub, new HashSet<TransactionSequence> { unproven });
                scrub.Commit();
                unprovenTombstones.Clear();
            }

            if (round == rounds / 3)
            {
                oldReader = await harness.BeginAsync();
                oldModel = new Dictionary<ulong, long>(live);
            }

            if (round % 5 == 4 || round == rounds - 1)
            {
                var reader = await harness.BeginAsync();
                await VerifyAsync(index, reader, live, Key, domain, random);
                await harness.RollbackAsync(reader);

                if (oldReader is not null)
                {
                    await VerifyAsync(index, oldReader, oldModel!, Key, domain, random);
                }
            }
        }
    }

    private static async Task VerifyAsync(
        IIndex index, TransactionContext reader, IReadOnlyDictionary<ulong, long> model,
        Func<long, IndexKey> keyOf, int domain, Random random)
    {
        var scan = await ReadAsync(index, reader, IndexKeyRange.All);

        // The scan is ordered and holds exactly the model's entries.
        ShouldBeOrdered(scan);
        scan.Select(entry => entry.Reference).Order().ShouldBe(model.Keys.Order());
        foreach (var (key, reference) in scan)
        {
            key.ShouldBe(keyOf(model[reference]).Encoded.ToArray());
        }

        // Every equality seek (and two keys outside the domain) equals the scan.
        for (long value = -1; value <= domain; value++)
        {
            await SeekShouldMatchScanAsync(index, reader, scan, Exactly(keyOf(value)));
        }

        // Random ranges: open or bounded on either side, either inclusivity.
        for (int i = 0; i < 8; i++)
        {
            long low = random.Next(-1, domain + 1);
            long high = random.Next((int)low, domain + 1);
            var range = new IndexKeyRange(
                random.Next(6) == 0 ? null : keyOf(low),
                random.Next(6) == 0 ? null : keyOf(high),
                random.Next(2) == 0,
                random.Next(2) == 0);
            await SeekShouldMatchScanAsync(index, reader, scan, range);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Duplicates: randomized unique workload — updates, deletes, and rejected duplicates agree with the model")]
    [InlineData(1159, 0)]
    [InlineData(64, 200)]
    [InlineData(2026, 700)]
    public async Task RandomizedUniqueWorkload_ShouldEnforceUniquenessAcrossDeadVersions(int seed, int padding)
    {
        // Every committed update leaves a dead version of the key behind (the SQL
        // UPDATE shape), so hot keys grow long runs of dead versions that split
        // between leaves while exactly one version stays live.
        var (harness, index) = await CreateIndexAsync(unique: true);
        await using var harnessLifetime = harness;

        var random = new Random(seed);
        const int domain = 40;
        IndexKey Key(long value) => PaddedKey(value, padding);
        long NextValue() => random.NextDouble() < 0.5 ? random.Next(3) : random.Next(domain);

        var live = new Dictionary<long, ulong>(); // value → its one live reference
        ulong nextReference = 1;

        for (int step = 0; step < 2_500; step++)
        {
            long value = NextValue();
            var transaction = await harness.BeginAsync();

            if (live.TryGetValue(value, out ulong current))
            {
                double choice = random.NextDouble();
                if (choice < 0.6)
                {
                    ulong successor = nextReference++;
                    await index.DeleteAsync(transaction, Key(value), current);
                    await index.InsertAsync(transaction, Key(value), successor);
                    if (random.NextDouble() < 0.9)
                    {
                        await harness.CommitAsync(transaction);
                        live[value] = successor;
                    }
                    else
                    {
                        await harness.RollbackAsync(transaction);
                    }
                }
                else if (choice < 0.85)
                {
                    await Should.ThrowAsync<IndexUniqueViolationException>(
                        async () => await index.InsertAsync(transaction, Key(value), nextReference++),
                        $"step {step}: key {value} is live as {current}");
                    await harness.RollbackAsync(transaction);
                }
                else
                {
                    await index.DeleteAsync(transaction, Key(value), current);
                    await harness.CommitAsync(transaction);
                    live.Remove(value);
                }
            }
            else
            {
                ulong reference = nextReference++;
                await index.InsertAsync(transaction, Key(value), reference);
                await harness.CommitAsync(transaction);
                live[value] = reference;
            }

            if (step % 250 == 249)
            {
                var reader = await harness.BeginAsync();
                var scan = await ReadAsync(index, reader, IndexKeyRange.All);
                ShouldBeOrdered(scan);
                scan.Select(entry => entry.Reference).Order().ShouldBe(live.Values.Order(), $"step {step}");

                for (long candidate = 0; candidate < domain; candidate++)
                {
                    var expected = live.TryGetValue(candidate, out ulong reference) ? new[] { reference } : Array.Empty<ulong>();
                    (await ReadAsync(index, reader, Exactly(Key(candidate)))).Select(entry => entry.Reference)
                        .ShouldBe(expected, $"step {step}: key {candidate}");
                }

                await harness.RollbackAsync(reader);
            }
        }
    }
}
