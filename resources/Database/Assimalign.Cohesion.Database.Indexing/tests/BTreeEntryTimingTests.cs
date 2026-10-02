using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

/// <summary>
/// Runs timing tests alone, so other suites do not compete for the CPU while they
/// measure.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    /// <summary>
    /// The collection name.
    /// </summary>
    public const string Name = "Indexing timing";
}

/// <summary>
/// Timing guards for #1194's acceptance: an operation that targets one entry of a key
/// descends to it, so its cost does not grow with the length of the key's run. Each
/// guard compares the cost per operation inside a 40,000-entry run with the cost inside
/// a 2,500-entry run of the same tree — a growth ratio, not an absolute time, so CPU
/// speed, build configuration and load cancel out. A walk of the run, as before #1194,
/// grows with the run: sixteen times the run, about sixteen times the cost. A descent
/// costs the same in both runs: they share the tree, its height and its cached pages.
/// The guard allows four.
/// </summary>
/// <remarks>
/// Entry references are in insertion order, and every round targets a contiguous
/// block of 400 references from the middle of each run — the block a walk from the
/// run's start reaches only after half the run — in shuffled order. A block spans the
/// same few leaves in either run, so the per-page cost of a transaction's first write
/// to a page (the storage layer's before-image) is the same for both, and each round
/// rolls back, so every round starts from the same tree. The two runs are measured in
/// alternation, and each takes the fastest of five rounds, which discards rounds a
/// collection or a busy machine slowed down.
/// </remarks>
[Collection(TimingCollection.Name)]
public class BTreeEntryTimingTests
{
    private const int shortRun = 2_500;
    private const int longRun = 40_000;
    private const int block = 400;
    private const int rounds = 5;
    private const double allowedGrowth = 4.0;

    private static readonly IndexKey shortKey = IndexKey.FromInt64(25);
    private static readonly IndexKey longKey = IndexKey.FromInt64(400);

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
                await index.InsertVersionAsync(build, shortKey, (ulong)i, writer, stamp);
            }
            for (int i = 0; i < longRun; i++)
            {
                await index.InsertVersionAsync(build, longKey, (ulong)(shortRun + i), writer, stamp);
            }
            build.Commit();
        }

        var small = new Run(harness, index, operation, shortKey, 0, shortRun, writer, deleter);
        var large = new Run(harness, index, operation, longKey, shortRun, longRun, writer, deleter);

        // Warm up both paths (JIT, page cache) and check the operation does its work.
        await small.MeasureAsync(verify: true);
        await large.MeasureAsync(verify: true);

        // Act
        double smallest = double.MaxValue;
        double largest = double.MaxValue;
        for (int round = 0; round < rounds; round++)
        {
            smallest = Math.Min(smallest, await small.MeasureAsync(verify: false));
            largest = Math.Min(largest, await large.MeasureAsync(verify: false));
        }

        // Assert
        double growth = largest / smallest;
        growth.ShouldBeLessThan(allowedGrowth,
            $"{operation}: {smallest:F2} us per operation in the {shortRun:N0}-entry run, {largest:F2} us in the {longRun:N0}-entry run");
    }

    private static async Task<TransactionSequence> CommittedAsync(IndexTestHarness harness)
    {
        var context = await harness.BeginAsync();
        await harness.CommitAsync(context);
        return context.Sequence;
    }

    /// <summary>
    /// One key's run, and the timed operation over the middle block of the run.
    /// </summary>
    private sealed class Run
    {
        private readonly IndexTestHarness _harness;
        private readonly IIndex _index;
        private readonly Operation _operation;
        private readonly IndexKey _key;
        private readonly int _length;
        private readonly TransactionSequence _writer;
        private readonly TransactionSequence _deleter;
        private readonly ulong[] _targets;

        internal Run(IndexTestHarness harness, IIndex index, Operation operation, IndexKey key, int firstReference, int length,
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
        /// and returns the microseconds per operation.
        /// </summary>
        internal async Task<double> MeasureAsync(bool verify)
        {
            long start;
            double elapsed;

            if (_operation == Operation.Delete)
            {
                var transaction = await _harness.BeginAsync();
                start = Stopwatch.GetTimestamp();
                foreach (ulong reference in _targets)
                {
                    await _index.DeleteAsync(transaction, _key, reference);
                }
                elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds;

                if (verify)
                {
                    (await CountVisibleAsync(transaction)).ShouldBe(_length - block);
                }

                await _harness.RollbackAsync(transaction);
            }
            else
            {
                using var bracket = _harness.Storage.BeginTransaction();
                start = Stopwatch.GetTimestamp();
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
                elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds;

                if (verify)
                {
                    var reader = await _harness.BeginAsync();
                    (await CountVisibleAsync(reader)).ShouldBe(_operation == Operation.Erase ? _length - block : block);
                    await _harness.RollbackAsync(reader);
                }

                bracket.Rollback();
            }

            return elapsed / block;
        }

        private async Task<int> CountVisibleAsync(ITransactionContext reader)
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
