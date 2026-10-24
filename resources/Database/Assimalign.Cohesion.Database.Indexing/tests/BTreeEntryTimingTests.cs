using System;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

/// <summary>
/// Physical-work guards for #1194: entry lookups descend directly regardless of duplicate
/// run length, and newest-version lookups avoid walking old versions. Counters observe every
/// entry-directory read; neither elapsed time nor scheduling decides the assertion (#1285).
/// </summary>
[Collection(TimingCollection.Name)]
public class BTreeEntryTimingTests
{
    private const int shortRun = 2_500;
    private const int longRun = 40_000;
    private const int block = 400;
    private const double allowedGrowth = 4.0;
    private const int fewVersions = 16;
    private const int manyVersions = 2_048;
    private const double allowedVersionGrowth = 2.5;

    private static readonly IndexKey _shortKey = IndexKey.FromInt64(25);
    private static readonly IndexKey _longKey = IndexKey.FromInt64(400);

    public enum Operation
    {
        Delete,
        Erase,
        ClearDeleter,
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Entry order: delete, erase and clear-deleter in a 40,000-entry run cost what they cost in a 2,500-entry run (#1194)")]
    [InlineData(Operation.Delete)]
    [InlineData(Operation.Erase)]
    [InlineData(Operation.ClearDeleter)]
    public async Task EntryLookup_InsideLongRun_ShouldNotGrowWithTheRun(Operation operation)
    {
        // Arrange: one tree, two runs; the long run's references follow the short run's.
        var harness = new IndexTestHarness();
        await using var harnessLifetime = harness;
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("ix_timing"));
        await harness.CommitAsync(setup);

        var writer = await CommittedAsync(harness);
        var deleter = await CommittedAsync(harness);

        // Clear-deleter needs tombstones to clear; the other operations live entries.
        var stamp = operation == Operation.ClearDeleter ? deleter : TransactionSequence.None;
        using (var build = harness.Storage.BeginTransaction())
        {
            for (int i = 0; i < shortRun; i++)
            {
                await index.InsertVersionAsync(build, _shortKey, (ulong)i, writer, stamp);
            }
            for (int i = 0; i < longRun; i++)
            {
                await index.InsertVersionAsync(build, _longKey, (ulong)(shortRun + i), writer, stamp);
            }
            build.Commit();
        }

        var small = new Run(harness, index, operation, _shortKey, 0, shortRun, writer, deleter);
        var large = new Run(harness, index, operation, _longKey, shortRun, longRun, writer, deleter);

        // Verify the operation before comparing physical work.
        await small.MeasureAsync(verify: true);
        await large.MeasureAsync(verify: true);

        // Act
        double smallest = await small.MeasureAsync(verify: false);
        double largest = await large.MeasureAsync(verify: false);

        // Assert
        smallest.ShouldBeGreaterThan(0);
        largest.ShouldBeGreaterThan(0);
        double growth = largest / smallest;
        growth.ShouldBeLessThan(allowedGrowth,
            $"{operation}: {smallest:F2} directory reads per operation in the {shortRun:N0}-entry run, {largest:F2} reads in the {longRun:N0}-entry run");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Entry order: delete and clear-deleter of a reference with 2,048 versions cost what they cost with 16 (#1194)")]
    [InlineData(Operation.Delete)]
    [InlineData(Operation.ClearDeleter)]
    public async Task ReferenceLookup_ManyVersions_ShouldNotGrowWithTheVersions(Operation operation)
    {
        // Arrange: one tree, two keys. Under each, 16 target references whose record
        // slots were reused: every version but the newest is dead (deleted by the next
        // version's writer), and the newest is live (delete) or carries the tombstone
        // clear-deleter removes. The few-versions key has 16 versions per target and
        // fifteen filler references between targets; the many-versions key has 2,048
        // versions per target. Either way every target's newest version sits on its own
        // leaf (a leaf holds at most 224 of these entries), so both pay one first write
        // to a page per operation. Before the lookups read a reference's versions newest
        // first, each walked every dead version: 128 times the versions, about 128 times
        // the walk.
        var harness = new IndexTestHarness();
        await using var harnessLifetime = harness;
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("ix_versions"));
        await harness.CommitAsync(setup);

        var writers = new TransactionSequence[manyVersions];
        for (int i = 0; i < writers.Length; i++)
        {
            writers[i] = await CommittedAsync(harness);
        }

        var deleter = await CommittedAsync(harness);
        var newestDeleter = operation == Operation.ClearDeleter ? deleter : TransactionSequence.None;

        var few = new VersionedRun(harness, index, operation, IndexKey.FromInt64(1), fewVersions, referencesPerTarget: 16, deleter);
        var many = new VersionedRun(harness, index, operation, IndexKey.FromInt64(2), manyVersions, referencesPerTarget: 1, deleter);
        using (var build = harness.Storage.BeginTransaction())
        {
            await few.BuildAsync(build, writers, newestDeleter);
            await many.BuildAsync(build, writers, newestDeleter);
            build.Commit();
        }

        await few.MeasureAsync(verify: true);
        await many.MeasureAsync(verify: true);

        // Act
        double smallest = await few.MeasureAsync(verify: false);
        double largest = await many.MeasureAsync(verify: false);

        // Assert
        smallest.ShouldBeGreaterThan(0);
        largest.ShouldBeGreaterThan(0);
        double growth = largest / smallest;
        growth.ShouldBeLessThan(allowedVersionGrowth,
            $"{operation}: {smallest:F2} directory reads per operation with {fewVersions} versions per reference, {largest:F2} reads with {manyVersions:N0}");
    }

    private sealed class EntryReadCounter : IDisposable
    {
        private long _reads;
        internal EntryReadCounter() { BTreeIndex.EntryAccessed = () => Interlocked.Increment(ref _reads); }
        internal long Count => Interlocked.Read(ref _reads);
        internal void Reset() => Interlocked.Exchange(ref _reads, 0);
        public void Dispose() => BTreeIndex.EntryAccessed = null;
    }
    private static async Task<TransactionSequence> CommittedAsync(IndexTestHarness harness)
    {
        var context = await harness.BeginAsync();
        await harness.CommitAsync(context);
        return context.Sequence;
    }

    /// <summary>
    /// The target references of one key, each with a history of reused-slot versions,
    /// and the timed operation over the targets' newest versions.
    /// </summary>
    private sealed class VersionedRun
    {
        private const int targets = 16;

        private readonly IndexTestHarness _harness;
        private readonly BTreeIndex _index;
        private readonly Operation _operation;
        private readonly IndexKey _key;
        private readonly int _versions;
        private readonly int _referencesPerTarget;
        private readonly TransactionSequence _deleter;
        private readonly ulong[] _targets;

        internal VersionedRun(IndexTestHarness harness, BTreeIndex index, Operation operation, IndexKey key, int versions,
            int referencesPerTarget, TransactionSequence deleter)
        {
            _harness = harness;
            _index = index;
            _operation = operation;
            _key = key;
            _versions = versions;
            _referencesPerTarget = referencesPerTarget;
            _deleter = deleter;

            var random = new Random(versions);
            _targets = Enumerable.Range(0, targets).Select(i => (ulong)(i * referencesPerTarget)).OrderBy(_ => random.Next()).ToArray();
        }

        private int References => targets * _referencesPerTarget;

        /// <summary>
        /// Inserts every reference's versions: version <c>v</c> written by
        /// <c>writers[v]</c> and deleted by <c>writers[v + 1]</c>; the newest deleted
        /// by <paramref name="newestDeleter"/>.
        /// </summary>
        internal async Task BuildAsync(StorageTransaction build, TransactionSequence[] writers, TransactionSequence newestDeleter)
        {
            for (int reference = 0; reference < References; reference++)
            {
                for (int version = 0; version < _versions; version++)
                {
                    var deleter = version == _versions - 1 ? newestDeleter : writers[version + 1];
                    await _index.InsertVersionAsync(build, _key, (ulong)reference, writers[version], deleter);
                }
            }
        }

        /// <summary>
        /// Runs the operation on every target inside a transaction that then rolls back,
        /// and returns the physical entry-directory reads per operation.
        /// </summary>
        internal async Task<double> MeasureAsync(bool verify)
        {
            using var counter = new EntryReadCounter();
            double reads;

            if (_operation == Operation.Delete)
            {
                var transaction = await _harness.BeginAsync();
                counter.Reset();
                foreach (ulong reference in _targets)
                {
                    await _index.DeleteAsync(transaction, _key, reference);
                }
                reads = counter.Count;

                if (verify)
                {
                    (await CountVisibleAsync(transaction)).ShouldBe(References - targets);
                }

                await _harness.RollbackAsync(transaction);
            }
            else
            {
                using var bracket = _harness.Storage.BeginTransaction();
                counter.Reset();
                foreach (ulong reference in _targets)
                {
                    await _index.ClearDeleterAsync(bracket, _key, reference, _deleter);
                }
                reads = counter.Count;

                if (verify)
                {
                    var reader = await _harness.BeginAsync();
                    (await CountVisibleAsync(reader)).ShouldBe(targets);
                    await _harness.RollbackAsync(reader);
                }

                bracket.Rollback();
            }

            return reads / targets;
        }

        private async Task<int> CountVisibleAsync(TransactionContext reader)
        {
            int count = 0;
            await using var cursor = _index.OpenCursor(reader, new IndexKeyRange(_key, _key, true, true));
            while (await cursor.MoveNextAsync())
            {
                count++;
            }

            return count;
        }
    }

    /// <summary>
    /// One key's run, and the timed operation over the middle block of the run.
    /// </summary>
    private sealed class Run
    {
        private readonly IndexTestHarness _harness;
        private readonly BTreeIndex _index;
        private readonly Operation _operation;
        private readonly IndexKey _key;
        private readonly int _length;
        private readonly TransactionSequence _writer;
        private readonly TransactionSequence _deleter;
        private readonly ulong[] _targets;

        internal Run(IndexTestHarness harness, BTreeIndex index, Operation operation, IndexKey key, int firstReference, int length,
            TransactionSequence writer, TransactionSequence deleter)
        {
            _harness = harness;
            _index = index;
            _operation = operation;
            _key = key;
            _length = length;
            _writer = writer;
            _deleter = deleter;

            var random = new Random(length);
            int first = firstReference + length / 2 - block / 2;
            _targets = Enumerable.Range(first, block).Select(i => (ulong)i).OrderBy(_ => random.Next()).ToArray();
        }

        /// <summary>
        /// Runs the operation over the block inside a transaction that then rolls back,
        /// and returns the physical entry-directory reads per operation.
        /// </summary>
        internal async Task<double> MeasureAsync(bool verify)
        {
            using var counter = new EntryReadCounter();
            double reads;

            if (_operation == Operation.Delete)
            {
                var transaction = await _harness.BeginAsync();
                counter.Reset();
                foreach (ulong reference in _targets)
                {
                    await _index.DeleteAsync(transaction, _key, reference);
                }
                reads = counter.Count;

                if (verify)
                {
                    (await CountVisibleAsync(transaction)).ShouldBe(_length - block);
                }

                await _harness.RollbackAsync(transaction);
            }
            else
            {
                using var bracket = _harness.Storage.BeginTransaction();
                counter.Reset();
                foreach (ulong reference in _targets)
                {
                    if (_operation == Operation.Erase)
                    {
                        await _index.EraseAsync(bracket, _key, reference, _writer);
                    }
                    else
                    {
                        await _index.ClearDeleterAsync(bracket, _key, reference, _deleter);
                    }
                }
                reads = counter.Count;

                if (verify)
                {
                    var reader = await _harness.BeginAsync();
                    (await CountVisibleAsync(reader)).ShouldBe(_operation == Operation.Erase ? _length - block : block);
                    await _harness.RollbackAsync(reader);
                }

                bracket.Rollback();
            }

            return reads / block;
        }

        private async Task<int> CountVisibleAsync(TransactionContext reader)
        {
            int count = 0;
            await using var cursor = _index.OpenCursor(reader, new IndexKeyRange(_key, _key, true, true));
            while (await cursor.MoveNextAsync())
            {
                count++;
            }

            return count;
        }
    }
}
