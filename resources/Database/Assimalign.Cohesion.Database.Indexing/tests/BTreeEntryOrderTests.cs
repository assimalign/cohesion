using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

/// <summary>
/// The entry order of #1194: B-tree entries are ordered by their identity
/// <c>(key, entry reference, writer)</c>, separators carry as much of that identity as
/// they need, and every lookup that targets one entry — delete, erase, clear-deleter —
/// descends to it. Seeks still return every entry of a key, and the order survives
/// splits, rollback, logical undo and crash recovery.
/// </summary>
public class BTreeEntryOrderTests
{
    private static async Task<(IndexTestHarness Harness, IIndex Index)> CreateIndexAsync(IndexTestHarness? harness = null, bool unique = false)
    {
        harness ??= new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(
            setup, objectId: 1, new IndexDefinition("ix_order", IndexKind.BTree, unique));
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
    /// An order-preserving key for <paramref name="value"/> followed by
    /// <paramref name="padding"/> filler bytes, which controls how many entries a leaf
    /// holds.
    /// </summary>
    private static IndexKey PaddedKey(long value, int padding)
    {
        var bytes = new byte[sizeof(long) + padding];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, (ulong)value ^ 0x8000_0000_0000_0000UL);
        bytes.AsSpan(sizeof(long)).Fill(0x5A);
        return new IndexKey(bytes);
    }

    private static async Task<TransactionSequence> CommittedSequenceAsync(IndexTestHarness harness)
    {
        var context = await harness.BeginAsync();
        await harness.CommitAsync(context);
        return context.Sequence;
    }

    private static void ShouldBeInEntryOrder(List<(byte[] Key, ulong Reference)> scan)
    {
        for (int i = 1; i < scan.Count; i++)
        {
            int comparison = scan[i - 1].Key.AsSpan().SequenceCompareTo(scan[i].Key);
            if (comparison > 0 || (comparison == 0 && scan[i - 1].Reference >= scan[i].Reference))
            {
                throw new ShouldAssertException(
                    $"Entry order broken at position {i}: {Convert.ToHexString(scan[i - 1].Key)}:{scan[i - 1].Reference} " +
                    $"precedes {Convert.ToHexString(scan[i].Key)}:{scan[i].Reference}.");
            }
        }
    }

    // ── Order ───────────────────────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Entry order: a key's entries come back in entry-reference order whatever order they arrived in (#1194)")]
    public async Task Seek_DuplicatesInsertedOutOfOrder_ShouldReturnReferenceOrder()
    {
        // Arrange: 3,000 entries of one key, inserted with shuffled references, with
        // neighbours on both sides; the run spans many leaves.
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        var random = new Random(1194);
        var references = Enumerable.Range(0, 3_000).Select(i => (ulong)i * 7 + 1).OrderBy(_ => random.Next()).ToList();
        var writer = await harness.BeginAsync();
        foreach (ulong reference in references)
        {
            await index.InsertAsync(writer, PaddedKey(50, 40), reference);
            if (reference % 5 == 1)
            {
                await index.InsertAsync(writer, PaddedKey(49, 40), reference);
                await index.InsertAsync(writer, PaddedKey(51, 40), reference);
            }
        }
        await harness.CommitAsync(writer);

        // Act
        var reader = await harness.BeginAsync();
        var run = await ReadAsync(index, reader, Exactly(PaddedKey(50, 40)));
        var reversed = await ReadAsync(index, reader, Exactly(PaddedKey(50, 40)), reverse: true);
        var scan = await ReadAsync(index, reader, IndexKeyRange.All);

        // Assert
        run.Select(entry => entry.Reference).ShouldBe(references.Order());
        reversed.Select(entry => entry.Reference).ShouldBe(references.OrderDescending());
        ShouldBeInEntryOrder(scan);
        scan.Count.ShouldBe(3_000 + 2 * references.Count(reference => reference % 5 == 1));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Entry order: an entry identity that is already present is refused, through both insert paths")]
    public async Task Insert_DuplicateIdentity_ShouldThrowAndChangeNothing()
    {
        // Arrange
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;
        var committed = await CommittedSequenceAsync(harness);

        using (var build = harness.Storage.BeginTransaction())
        {
            await index.InsertVersionAsync(build, IndexKey.FromInt64(5), 40, committed, TransactionSequence.None);
            build.Commit();
        }

        // Act: the build path repeats (key, reference, writer); a transaction inserts
        // the same mapping twice under its own sequence.
        IndexException? rebuilt;
        using (var build = harness.Storage.BeginTransaction())
        {
            rebuilt = await Should.ThrowAsync<IndexException>(async () =>
                await index.InsertVersionAsync(build, IndexKey.FromInt64(5), 40, committed, TransactionSequence.None));
            build.Rollback();
        }

        var transaction = await harness.BeginAsync();
        await index.InsertAsync(transaction, IndexKey.FromInt64(5), 41);
        var twice = await Should.ThrowAsync<IndexException>(async () => await index.InsertAsync(transaction, IndexKey.FromInt64(5), 41));
        await harness.RollbackAsync(transaction);

        // Assert: the same reference under another writer is a different entry.
        rebuilt.Message.ShouldContain("must be unique");
        twice.Message.ShouldContain("must be unique");

        var other = await harness.BeginAsync();
        await index.InsertAsync(other, IndexKey.FromInt64(5), 40);
        await harness.CommitAsync(other);

        var reader = await harness.BeginAsync();
        (await ReadAsync(index, reader, Exactly(IndexKey.FromInt64(5)))).Select(entry => entry.Reference).ShouldBe(new[] { 40UL, 40UL });
    }

    // ── Separators ──────────────────────────────────────────────────────

    public enum RunShape
    {
        DistinctKeys,
        DistinctReferences,
        DistinctWriters,
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Entry order: separators keep only the tiebreaker that separates their neighbours")]
    [InlineData(RunShape.DistinctKeys)]
    [InlineData(RunShape.DistinctReferences)]
    [InlineData(RunShape.DistinctWriters)]
    public async Task Split_Separators_ShouldKeepOnlyTheTiebreakerTheyNeed(RunShape shape)
    {
        // Arrange: 600 entries with 300-byte keys (about 25 to a leaf), which differ in
        // their key, only in their entry reference, or only in their writer (a record
        // slot reused under the same key).
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        var shared = await CommittedSequenceAsync(harness);
        var writers = new List<TransactionSequence>();
        for (int i = 0; i < 600; i++)
        {
            writers.Add(shape == RunShape.DistinctWriters ? await CommittedSequenceAsync(harness) : shared);
        }

        using (var build = harness.Storage.BeginTransaction())
        {
            for (int i = 0; i < 600; i++)
            {
                var key = shape == RunShape.DistinctKeys ? PaddedKey(i, 292) : PaddedKey(7, 292);
                ulong reference = shape == RunShape.DistinctReferences ? (ulong)i : 99;
                await index.InsertVersionAsync(build, key, reference, writers[i], TransactionSequence.None);
            }
            build.Commit();
        }

        // Act
        long root = ((IIndexRegistry)harness.IndexManager).ExportRegistrations().Single().RootPageId;
        var separators = ReadSeparators(harness.Storage, root);

        // Assert
        separators.Count.ShouldBeGreaterThan(10);
        var expected = shape switch
        {
            RunShape.DistinctKeys => 0,
            RunShape.DistinctReferences => 1,
            _ => 2,
        };
        separators.ShouldAllBe(separator => separator.Tiebreaker == expected);
        if (shape == RunShape.DistinctKeys)
        {
            // Keys differ in their eight value bytes, so no separator needs the padding.
            separators.ShouldAllBe(separator => separator.KeyLength <= 8);
        }
        else
        {
            separators.ShouldAllBe(separator => separator.KeyLength == 300);
        }

        var reader = await harness.BeginAsync();
        var scan = await ReadAsync(index, reader, IndexKeyRange.All);
        scan.Count.ShouldBe(600);
        if (shape != RunShape.DistinctWriters)
        {
            ShouldBeInEntryOrder(scan);
        }
    }

    /// <summary>
    /// Reads the separators of every internal node under <paramref name="root"/> from
    /// the pages (format 2: kind at 3, entry count at 4, leftmost child at 24, directory
    /// at 32; an internal entry is <c>[u16 keyLen | tiebreaker &lt;&lt; 14][key][8 bytes
    /// per tiebreaker attribute][i64 child]</c>): the tiebreaker attributes each keeps
    /// and its key length. Every page visited must carry the format-2 stamp.
    /// </summary>
    private static List<(int Tiebreaker, int KeyLength)> ReadSeparators(Storage.Storage storage, long root)
    {
        var separators = new List<(int, int)>();
        var pending = new Stack<long>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            long pageId = pending.Pop();
            using var handle = storage.PageManager.GetPage(pageId);
            var body = handle.Page.AsBodySpan();
            LegacyBTreePages.IsFormat2Node(body).ShouldBeTrue($"page {pageId} carries the format-2 stamp");

            if (body[3] != 2)
            {
                continue; // a leaf
            }

            pending.Push(BinaryPrimitives.ReadInt64LittleEndian(body[24..]));
            int count = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
            for (int i = 0; i < count; i++)
            {
                int entry = BinaryPrimitives.ReadUInt16LittleEndian(body[(32 + 2 * i)..]);
                int field = BinaryPrimitives.ReadUInt16LittleEndian(body[entry..]);
                int tiebreaker = field >> 14;
                int keyLength = field & 0x3FFF;
                separators.Add((tiebreaker, keyLength));
                pending.Push(BinaryPrimitives.ReadInt64LittleEndian(body[(entry + 2 + keyLength + 8 * tiebreaker)..]));
            }
        }

        return separators;
    }

    // ── Versions of one reference ───────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Entry order: delete, erase and clear-deleter find their version among a reference's versions across leaf splits")]
    public async Task ReferenceVersions_AcrossLeafSplits_ShouldBeFoundExactly()
    {
        // Arrange: 600 versions of one (key, reference) — a record slot reused under one
        // key, each version deleted by its successor's writer, the last one live. With
        // 200-byte keys they span about seventeen leaves, split by writer.
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        const ulong reference = 31;
        var key = PaddedKey(12, 192);
        var writers = new List<TransactionSequence>();
        for (int i = 0; i < 600; i++)
        {
            writers.Add(await CommittedSequenceAsync(harness));
        }

        using (var build = harness.Storage.BeginTransaction())
        {
            // Inserted in shuffled order: the tree orders them by writer.
            foreach (int i in Enumerable.Range(0, 600).OrderBy(i => (i * 7919) % 600))
            {
                var deleter = i == 599 ? TransactionSequence.None : writers[i + 1];
                await index.InsertVersionAsync(build, key, reference, writers[i], deleter);
            }
            build.Commit();
        }

        async Task<int> VisibleCountAsync()
        {
            var reader = await harness.BeginAsync();
            int count = (await ReadAsync(index, reader, Exactly(key))).Count(entry => entry.Reference == reference);
            await harness.RollbackAsync(reader);
            return count;
        }

        (await VisibleCountAsync()).ShouldBe(1);

        // Act / Assert: clear-deleter restores exactly the version its deleter names.
        using (var undo = harness.Storage.BeginTransaction())
        {
            await index.ClearDeleterAsync(undo, key, reference, writers[301]);
            await index.ClearDeleterAsync(undo, key, reference, new TransactionSequence(999_999_999)); // no such deleter: a no-op
            undo.Commit();
        }
        (await VisibleCountAsync()).ShouldBe(2);

        // Erase removes exactly the version its writer names.
        using (var undo = harness.Storage.BeginTransaction())
        {
            await index.EraseAsync(undo, key, reference, writers[300]);
            await index.EraseAsync(undo, key, reference, writers[300]); // already gone: a no-op
            undo.Commit();
        }
        (await VisibleCountAsync()).ShouldBe(1);

        // Delete tombstones the one live version the snapshot sees, wherever it sits.
        var deleting = await harness.BeginAsync();
        await index.DeleteAsync(deleting, key, reference);
        (await ReadAsync(index, deleting, Exactly(key))).ShouldBeEmpty();
        await harness.CommitAsync(deleting);
        (await VisibleCountAsync()).ShouldBe(0);

        // The logical undo of that delete clears the tombstone again.
        using (var undo = harness.Storage.BeginTransaction())
        {
            await index.ClearDeleterAsync(undo, key, reference, deleting.Sequence);
            undo.Commit();
        }
        (await VisibleCountAsync()).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Entry order: delete and clear-deleter read a reference's versions newest first, across leaves an undo emptied")]
    public async Task ReferenceVersions_NewestFirst_ShouldCrossEmptiedLeaves()
    {
        // Arrange: 600 versions of (key, 31) — a record slot reused under one key, each
        // version deleted by its successor's writer — between one live version each of
        // references 30 and 32, and another key after them. With 200-byte keys a leaf
        // holds at most 35 entries.
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        const ulong reference = 31;
        var key = PaddedKey(12, 192);
        var writers = new List<TransactionSequence>();
        for (int i = 0; i < 600; i++)
        {
            writers.Add(await CommittedSequenceAsync(harness));
        }

        using (var build = harness.Storage.BeginTransaction())
        {
            await index.InsertVersionAsync(build, key, reference - 1, writers[0], TransactionSequence.None);
            foreach (int i in Enumerable.Range(0, 600).OrderBy(i => (i * 7919) % 600))
            {
                var deleter = i == 599 ? TransactionSequence.None : writers[i + 1];
                await index.InsertVersionAsync(build, key, reference, writers[i], deleter);
            }
            await index.InsertVersionAsync(build, key, reference + 1, writers[0], TransactionSequence.None);
            await index.InsertVersionAsync(build, PaddedKey(13, 192), reference, writers[0], TransactionSequence.None);
            build.Commit();
        }

        long root = ((IIndexRegistry)harness.IndexManager).ExportRegistrations().Single().RootPageId;

        async Task<List<ulong>> VisibleAsync()
        {
            var reader = await harness.BeginAsync();
            var references = (await ReadAsync(index, reader, Exactly(key))).Select(entry => entry.Reference).ToList();
            await harness.RollbackAsync(reader);
            return references;
        }

        // The newest 60 versions are erased — the undo of their aborted writers — which
        // empties whole leaves between the remaining versions and reference 32.
        using (var undo = harness.Storage.BeginTransaction())
        {
            for (int i = 540; i < 600; i++)
            {
                await index.EraseAsync(undo, key, reference, writers[i]);
            }
            undo.Commit();
        }
        ReadLeafChain(harness.Storage, root).Any(leaf => leaf.Count == 0).ShouldBeTrue("the erase empties a leaf on the chain");

        // Act / Assert: the aborted writer's tombstone on version 539 — now the newest —
        // is found behind the emptied leaves and cleared.
        using (var undo = harness.Storage.BeginTransaction())
        {
            await index.ClearDeleterAsync(undo, key, reference, writers[540]);
            undo.Commit();
        }
        (await VisibleAsync()).ShouldBe(new[] { reference - 1, reference, reference + 1 });

        // Delete tombstones that newest version.
        var deleting = await harness.BeginAsync();
        await index.DeleteAsync(deleting, key, reference);
        (await ReadAsync(index, deleting, Exactly(key))).Select(entry => entry.Reference).ShouldBe(new[] { reference - 1, reference + 1 });
        await harness.CommitAsync(deleting);
        (await VisibleAsync()).ShouldBe(new[] { reference - 1, reference + 1 });

        // An older version's stamp is still found: the walk reads past the newest.
        using (var undo = harness.Storage.BeginTransaction())
        {
            await index.ClearDeleterAsync(undo, key, reference, writers[301]);
            await index.ClearDeleterAsync(undo, key, reference, new TransactionSequence(999_999_999)); // no such deleter: a no-op
            undo.Commit();
        }
        (await VisibleAsync()).ShouldBe(new[] { reference - 1, reference, reference + 1 });

        // The neighbours are untouched by every lookup above.
        var neighbours = await harness.BeginAsync();
        await index.DeleteAsync(neighbours, key, reference + 1);
        await index.DeleteAsync(neighbours, key, reference - 1);
        await harness.CommitAsync(neighbours);
        (await VisibleAsync()).ShouldBe(new[] { reference });
        ShouldHaveConsistentLeafChain(harness.Storage, root);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Entry order: the largest entry reference's newest version is found")]
    public async Task ReferenceVersions_LargestReference_ShouldBeFound()
    {
        // Arrange: three versions of (key, ulong.MaxValue), whose end position is the
        // end of the key, followed by the next key.
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        var key = IndexKey.FromInt64(8);
        var first = await CommittedSequenceAsync(harness);
        var second = await CommittedSequenceAsync(harness);
        var third = await CommittedSequenceAsync(harness);
        using (var build = harness.Storage.BeginTransaction())
        {
            await index.InsertVersionAsync(build, key, ulong.MaxValue, first, second);
            await index.InsertVersionAsync(build, key, ulong.MaxValue, second, third);
            await index.InsertVersionAsync(build, key, ulong.MaxValue, third, TransactionSequence.None);
            await index.InsertVersionAsync(build, IndexKey.FromInt64(9), 0, first, TransactionSequence.None);
            build.Commit();
        }

        // Act
        var deleting = await harness.BeginAsync();
        await index.DeleteAsync(deleting, key, ulong.MaxValue);
        await harness.CommitAsync(deleting);

        using (var undo = harness.Storage.BeginTransaction())
        {
            await index.ClearDeleterAsync(undo, key, ulong.MaxValue, third);
            undo.Commit();
        }

        // Assert: the delete stamped the newest version, the undo restored the middle one.
        var reader = await harness.BeginAsync();
        (await ReadAsync(index, reader, Exactly(key))).Select(entry => entry.Reference).ShouldBe(new[] { ulong.MaxValue });
        (await ReadAsync(index, reader, Exactly(IndexKey.FromInt64(9)))).Count.ShouldBe(1);
    }

    /// <summary>
    /// Reads the leaf chain from the pages, left to right: each leaf's page and entry
    /// count.
    /// </summary>
    private static List<(long Page, int Count)> ReadLeafChain(Storage.Storage storage, long root)
    {
        var leaves = new List<(long, int)>();
        long current = LeftmostLeaf(storage, root);

        while (current >= 0)
        {
            using var handle = storage.PageManager.GetPage(current);
            var body = handle.Page.AsBodySpan();
            leaves.Add((current, BinaryPrimitives.ReadUInt16LittleEndian(body[4..])));
            current = BinaryPrimitives.ReadInt64LittleEndian(body[8..]);
        }

        return leaves;
    }

    /// <summary>
    /// Fails unless the leaf chain read right to left through the previous-leaf links
    /// is the chain read left to right through the next-leaf links, reversed — the
    /// newest-first lookups walk it leftward — and both ends are the tree's leftmost
    /// and rightmost leaves.
    /// </summary>
    private static void ShouldHaveConsistentLeafChain(Storage.Storage storage, long root)
    {
        var forward = ReadLeafChain(storage, root).Select(leaf => leaf.Page).ToList();
        var backward = new List<long>();
        long current = RightmostLeaf(storage, root);
        long next = -1;

        while (current >= 0)
        {
            using var handle = storage.PageManager.GetPage(current);
            var body = handle.Page.AsBodySpan();
            body[3].ShouldBe((byte)1, $"page {current} on the leaf chain is a leaf");
            BinaryPrimitives.ReadInt64LittleEndian(body[8..]).ShouldBe(next, $"leaf {current}'s next leaf");
            backward.Add(current);
            next = current;
            current = BinaryPrimitives.ReadInt64LittleEndian(body[16..]);
        }

        backward.AsEnumerable().Reverse().ShouldBe(forward);
    }

    private static long LeftmostLeaf(Storage.Storage storage, long root) => DescendEdge(storage, root, rightmost: false);

    private static long RightmostLeaf(Storage.Storage storage, long root) => DescendEdge(storage, root, rightmost: true);

    /// <summary>
    /// Descends from <paramref name="root"/> along the first or the last child of every
    /// internal node (format 2: kind at 3, entry count at 4, leftmost child at 24,
    /// directory at 32).
    /// </summary>
    private static long DescendEdge(Storage.Storage storage, long root, bool rightmost)
    {
        long current = root;

        while (true)
        {
            using var handle = storage.PageManager.GetPage(current);
            var body = handle.Page.AsBodySpan();
            if (body[3] == 1)
            {
                return current;
            }

            int count = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
            if (!rightmost || count == 0)
            {
                current = BinaryPrimitives.ReadInt64LittleEndian(body[24..]);
                continue;
            }

            int entry = BinaryPrimitives.ReadUInt16LittleEndian(body[(32 + 2 * (count - 1))..]);
            int field = BinaryPrimitives.ReadUInt16LittleEndian(body[entry..]);
            current = BinaryPrimitives.ReadInt64LittleEndian(body[(entry + 2 + (field & 0x3FFF) + 8 * (field >> 14))..]);
        }
    }

    // ── Randomized: keys that are prefixes of one another ───────────────

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Entry order: randomized prefix-sharing keys — every seek equals the scan and the model")]
    [InlineData(1194)]
    [InlineData(20261002)]
    [InlineData(77)]
    public async Task RandomizedPrefixKeys_SeeksShouldMatchScanAndModel(int seed)
    {
        // Keys of zero to five bytes over four byte values, so many keys are prefixes of
        // others and suffix-truncated separators fall between them: the byte-granular
        // truncation must keep every key on the right side of every separator.
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        var random = new Random(seed);
        byte[] alphabet = [0x00, 0x01, 0x80, 0xFF];
        byte[] NextKey()
        {
            var bytes = new byte[random.Next(6)];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = alphabet[random.Next(alphabet.Length)];
            }
            return bytes;
        }

        var live = new Dictionary<ulong, byte[]>();
        ulong nextReference = 1;

        for (int round = 0; round < 24; round++)
        {
            var transaction = await harness.BeginAsync();
            var inserted = new Dictionary<ulong, byte[]>();
            var deleted = new HashSet<ulong>();

            for (int op = random.Next(200, 600); op > 0; op--)
            {
                if (live.Count == 0 || random.NextDouble() < 0.75)
                {
                    byte[] key = NextKey();
                    ulong reference = nextReference++;
                    await index.InsertAsync(transaction, new IndexKey(key), reference);
                    inserted[reference] = key;
                }
                else
                {
                    var candidates = live.Keys.Where(reference => !deleted.Contains(reference)).ToList();
                    if (candidates.Count == 0)
                    {
                        continue;
                    }

                    ulong reference = candidates[random.Next(candidates.Count)];
                    await index.DeleteAsync(transaction, new IndexKey(live[reference]), reference);
                    deleted.Add(reference);
                }
            }

            if (random.NextDouble() < 0.8)
            {
                await harness.CommitAsync(transaction);
                foreach (var (reference, key) in inserted)
                {
                    live[reference] = key;
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

            if (round % 6 == 5)
            {
                await VerifyAsync(harness, index, live, random, NextKey);
            }
        }
    }

    private static async Task VerifyAsync(
        IndexTestHarness harness, IIndex index, IReadOnlyDictionary<ulong, byte[]> model, Random random, Func<byte[]> nextKey)
    {
        var reader = await harness.BeginAsync();
        var scan = await ReadAsync(index, reader, IndexKeyRange.All);

        ShouldBeInEntryOrder(scan);
        scan.Select(entry => entry.Reference).Order().ShouldBe(model.Keys.Order());
        foreach (var (key, reference) in scan)
        {
            key.ShouldBe(model[reference]);
        }

        // Every key the model holds, and fresh random ones, seek exactly what the scan holds.
        var probes = model.Values.Select(Convert.ToHexString).Distinct().Select(Convert.FromHexString).ToList();
        for (int i = 0; i < 40; i++)
        {
            probes.Add(nextKey());
        }

        foreach (byte[] probe in probes)
        {
            var expected = scan.Where(entry => entry.Key.AsSpan().SequenceEqual(probe)).Select(entry => entry.Reference).ToList();
            (await ReadAsync(index, reader, Exactly(new IndexKey(probe)))).Select(entry => entry.Reference)
                .ShouldBe(expected, $"seek {Convert.ToHexString(probe)}");
        }

        // Ranges between random keys, with either inclusivity; prefix pairs are common.
        for (int i = 0; i < 30; i++)
        {
            byte[] low = nextKey();
            byte[] high = nextKey();
            if (low.AsSpan().SequenceCompareTo(high) > 0)
            {
                (low, high) = (high, low);
            }

            var range = new IndexKeyRange(new IndexKey(low), new IndexKey(high), random.Next(2) == 0, random.Next(2) == 0);
            var expected = scan.Where(entry =>
            {
                int start = entry.Key.AsSpan().SequenceCompareTo(low);
                int end = entry.Key.AsSpan().SequenceCompareTo(high);
                return (start > 0 || (start == 0 && range.IsStartInclusive)) && (end < 0 || (end == 0 && range.IsEndInclusive));
            }).Select(entry => entry.Reference).ToList();

            (await ReadAsync(index, reader, range)).Select(entry => entry.Reference)
                .ShouldBe(expected, $"range {Convert.ToHexString(low)}..{Convert.ToHexString(high)}");
        }

        await harness.RollbackAsync(reader);

        // Splits, rolled-back splits and root growth keep both leaf links consistent.
        ShouldHaveConsistentLeafChain(harness.Storage, ((IIndexRegistry)harness.IndexManager).ExportRegistrations().Single().RootPageId);
    }

    // ── Recovery ────────────────────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Entry order: recovery replays a committed duplicate run and reverts an in-flight one, and lookups stay exact")]
    public async Task Recovery_DuplicateRun_ShouldReplayCommittedAndRevertInFlight()
    {
        // Arrange: one committed transaction inserts a 2,500-entry run of one key with
        // shuffled references and tombstones a fifth of it; an in-flight transaction then
        // splits the run further, tombstones more, is stolen to disk, and the process
        // crashes. (Everything committed rides the first transaction: the reopened
        // harness restarts its sequence space, so its first transaction sees sequence 1
        // as its own.)
        var data = new CrashSimulationStream(writeThrough: true);
        var journal = new CrashSimulationStream(writeThrough: false);
        var harness = new IndexTestHarness(data, journal);

        var key = PaddedKey(3, 24);
        var random = new Random(2026);
        var references = Enumerable.Range(1, 2_500).Select(i => (ulong)i).OrderBy(_ => random.Next()).ToList();
        var tombstoned = references.Where(reference => reference % 5 == 0).ToHashSet();

        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("ix_recovery"));
        foreach (ulong reference in references)
        {
            await index.InsertAsync(setup, key, reference);
        }
        foreach (ulong reference in tombstoned)
        {
            await index.DeleteAsync(setup, key, reference);
        }
        await harness.CommitAsync(setup);
        var registrations = ((IIndexRegistry)harness.IndexManager).ExportRegistrations();

        var doomed = await harness.BeginAsync();
        for (ulong reference = 10_001; reference <= 11_500; reference++)
        {
            await index.InsertAsync(doomed, key, reference);
        }
        foreach (ulong reference in references.Where(reference => reference % 7 == 0 && !tombstoned.Contains(reference)))
        {
            await index.DeleteAsync(doomed, key, reference);
        }
        harness.Storage.PageManager.FlushAll();

        byte[] crashedData = data.CaptureDurable();
        byte[] crashedJournal = journal.CaptureDurable();

        // Act
        await using var recovered = IndexTestHarness.Reopen(crashedData, crashedJournal, registrations);
        recovered.IndexManager.TryGetIndex(1, "ix_recovery", out var recoveredIndex).ShouldBeTrue();

        // Assert: exactly the committed survivors, in reference order.
        var survivors = references.Where(reference => !tombstoned.Contains(reference)).Order().ToList();
        var reader = await recovered.BeginAsync();
        (await ReadAsync(recoveredIndex, reader, Exactly(key))).Select(entry => entry.Reference).ShouldBe(survivors);
        await recovered.RollbackAsync(reader);
        ShouldHaveConsistentLeafChain(recovered.Storage, registrations.Single().RootPageId);

        // Exact lookups work on the recovered tree: a delete, then an erase of a new entry.
        var writer = await recovered.BeginAsync();
        await recoveredIndex.DeleteAsync(writer, key, survivors[survivors.Count / 2]);
        await recoveredIndex.InsertAsync(writer, key, 20_000);
        await recovered.CommitAsync(writer);

        using (var undo = recovered.Storage.BeginTransaction())
        {
            await recoveredIndex.EraseAsync(undo, key, 20_000, writer.Sequence);
            undo.Commit();
        }

        var after = await recovered.BeginAsync();
        (await ReadAsync(recoveredIndex, after, Exactly(key))).Select(entry => entry.Reference)
            .ShouldBe(survivors.Where(reference => reference != survivors[survivors.Count / 2]));
    }
}
