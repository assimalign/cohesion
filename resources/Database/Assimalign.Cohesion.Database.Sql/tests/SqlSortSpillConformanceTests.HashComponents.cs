using System;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Testing;

using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapPool;
using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapAcceptance;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Hash key rows: marginal live payload and identity maps fit RowBytes for every codec family (#1789)")]
    [InlineData("NULL")]
    [InlineData("TINYINT")]
    [InlineData("SMALLINT")]
    [InlineData("INT")]
    [InlineData("BIGINT")]
    [InlineData("REAL")]
    [InlineData("DOUBLE PRECISION")]
    [InlineData("NUMERIC(12,2)")]
    [InlineData("BOOLEAN")]
    [InlineData("DATE")]
    [InlineData("TIME")]
    [InlineData("TIMESTAMP")]
    [InlineData("TIMESTAMPTZ")]
    [InlineData("INTERVAL")]
    [InlineData("UUID")]
    [InlineData("TEXT")]
    [InlineData("BINARY")]
    [InlineData("CHAR")]
    [InlineData("WIDE")]
    [InlineData("BYTE")]
    [InlineData("USHORT")]
    [InlineData("UINT")]
    [InlineData("ULONG")]
    [InlineData("CLRCHAR")]
    public Task RowBytes_KeyFamilies_ShouldBoundMarginalHeap(string family)
    {
        // Arrange
        MeasureKeys(family);
        using var pool = new HashPoolEvents();
        using var images = new WindowPoolLease();
        images.Drain(pool);


        // Act
        var batch = new Batch(pool, _output, nameof(RowBytes_KeyFamilies_ShouldBoundMarginalHeap));
        var rounds = new List<Sample<(long Live, long Charged)>>();
        for (int index = 1; index <= 3; index++)
        {
            int round = index;
            var sample = batch.Measure($"family={family}|round={round}", readHeap =>
            {
                images.Drain(pool);
                var measured = MeasureKeys(family, readHeap: readHeap);
                _output.WriteLine($"KEY_ROUND|family={family}|round={round}|live={measured.Live}|charged={measured.Charged}");
                return measured;
            });
            rounds.Add(sample);
        }
        long Live() => rounds.Min(x => x.Value.Live);
        long Charged() => rounds[^1].Value.Charged;
        // Assert
        batch.Check(() => [Ceiling("key bytes per row", Live() / 3072, Charged() / 3072, Term.Maximum(rounds) / 3072)], () =>
        {
            long live = Live(), charged = Charged();
            _output.WriteLine($"KEY_CHARGE|{family}|live_per_row={live / 3072d}|charged_per_row={charged / 3072d}");
            (live / 3072).ShouldBeLessThanOrEqualTo(charged / 3072);
        });
        batch.Complete();
        return Task.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Live, long Charged) MeasureKeys(string family, Func<long>? readHeap = null)
    {
        var roots = new object?[4096][];
        SqlSelectMemory memory = default;
        long charge = 0;
        Fill(0, 1024);
        long small = (readHeap ?? LiveHeap.Read)();
        Fill(1024, 4096);
        long live = (readHeap ?? LiveHeap.Read)() - small;
        GC.KeepAlive(roots); GC.KeepAlive(memory);
        return (live, charge);

        void Fill(int first, int last)
        {
            for (int i = first; i < last; i++)
            {
                object? value = family switch
                {
                    "TEXT" => new string('x', 32), "WIDE" => new string('x', 256), "BINARY" => new byte[256],
                    "CHAR" => new SqlCharacter(new string('x', 128) + new string(' ', 32)),
                    _ => StateValue(family, i),
                };
                var row = new object?[] { value };
                long bytes = memory.RowBytes(row, out bool wide);
                if (first != 0) { charge += bytes; }
                if (wide) { memory.AddRow(row); }
                roots[i] = row;
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Hash entries: +32 entry, group array and +64 DISTINCT overheads bound marginal live bytes (#1789)")]
    public Task HashEntries_TwoPopulations_ShouldBoundMarginalHeap()
    {
        // Arrange / Act
        MeasureEntries();
        MeasureEntries(padded: true);
        // Unrelated pooled pages must stay rooted across every marginal heap sample.
        using var pool = new HashPoolEvents();
        using var images = new WindowPoolLease();
        images.Drain(pool);
        var batch = new Batch(pool, _output, nameof(HashEntries_TwoPopulations_ShouldBoundMarginalHeap));
        var rounds = new List<Sample<(long Entry, long Replay, long Fitting)>>();
        for (int index = 1; index <= 3; index++)
        {
            int round = index;
            var sample = batch.Measure($"round={round}|padded=False", readHeap =>
            {
                images.Drain(pool);
                var measured = MeasureEntries(readHeap: readHeap);
                _output.WriteLine($"ENTRY_ROUND|round={round}|entry={measured.Entry}|replay={measured.Replay}|fitting={measured.Fitting}");
                return measured;
            });
            rounds.Add(sample);
            batch.Check(() => [Floor("entry > 0", sample.Value.Entry, 0, sample.Term, true),
                Floor("replay > 0", sample.Value.Replay, 0, sample.Term, true),
                Floor("fitting > 0", sample.Value.Fitting, 0, sample.Term, true)], () =>
            {
                var measured = sample.Value;
                measured.Entry.ShouldBeGreaterThan(0);
                measured.Replay.ShouldBeGreaterThan(0);
                measured.Fitting.ShouldBeGreaterThan(0);
            });
            var padded = batch.Measure($"round={round}|padded=True", readHeap =>
            {
                images.Drain(pool);
                var mutant = MeasureEntries(padded: true, readHeap: readHeap);
                _output.WriteLine($"ENTRY_MUTANT|round={round}|entry={mutant.Entry}|bytes_per_entry={mutant.Entry / 3072}|added_per_entry=8");
                return mutant;
            });
            batch.Check(() =>
            {
                var mutant = padded.Value;
                double violation = Math.Max(Math.Max(mutant.Entry / 3072 - 32, (mutant.Replay - mutant.Entry) / 3072 - 64), Math.Max(mutant.Replay / 3072 - 96, mutant.Fitting / 3072 - 64));
                return[Floor("padded entry > 32", mutant.Entry / 3072, 32, padded.Term / 3072, true), Floor("padded charge violation", violation, 0, padded.Term / 3072, true)];
            }, () =>
            {
                var mutant = padded.Value;
                (mutant.Entry / 3072).ShouldBeGreaterThan(32);
                Should.Throw<ShouldAssertException>(() => CheckEntryCharges(mutant.Entry, mutant.Replay, mutant.Fitting));
            });
        }
        long Entry() => rounds.Min(x => x.Value.Entry);
        long Replay() => rounds.Min(x => x.Value.Replay);
        long Fitting() => rounds.Min(x => x.Value.Fitting);
        // Assert: preserve every per-entry charge, including the difference of minima.
        batch.Check(() =>
        {
            Term term = Term.Maximum(rounds);
            return[Ceiling("entry <= 32", Entry() / 3072, 32, term / 3072), Ceiling("replay overhead <= 64", (Replay() - Entry()) / 3072, 64, (term + term) / 3072), Ceiling("replay <= 96", Replay() / 3072, 96, term / 3072), Ceiling("fitting <= 64", Fitting() / 3072, 64, term / 3072)];
        }, () =>
        {
            long entry = Entry(), replay = Replay(), fitting = Fitting();
            _output.WriteLine($"ENTRY_CHARGE|entry={entry / 3072d}|replay={replay / 3072d}|distinct={fitting / 3072d}|bound=32/96/64");
            CheckEntryCharges(entry, replay, fitting);
        });
        batch.Complete();
        return Task.CompletedTask;
    }

    private static void CheckEntryCharges(long entry, long replay, long fitting)
    {
        // The contract is bytes per entry. Floor division excludes a constant sub-entry
        // heap artefact while an extra field on every entry still exceeds the same bound.
        (entry / 3072).ShouldBeLessThanOrEqualTo(32);
        ((replay - entry) / 3072).ShouldBeLessThanOrEqualTo(64);
        (replay / 3072).ShouldBeLessThanOrEqualTo(96);
        (fitting / 3072).ShouldBeLessThanOrEqualTo(64);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Entry, long Replay, long Fitting) MeasureEntries(bool padded = false, Func<long>? readHeap = null)
    {
        bool diagnose = Environment.GetEnvironmentVariable("T3_ENTRY_HEAP_DIAGNOSTICS") == "1";
        var keys = new object?[4096][]; var entries = new object?[4096];
        for (int i = 0; i < keys.Length; i++) { keys[i] = [i]; }
        for (int i = 0; i < 1024; i++) { entries[i] = Entry(padded, i); }
        long small = (readHeap ?? LiveHeap.Read)();
        var generationsBefore = diagnose ? EntryGenerations() : default;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long processBefore = GC.GetTotalAllocatedBytes(precise: true);
        for (int i = 1024; i < 4096; i++) { entries[i] = Entry(padded, i); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        long processAllocated = GC.GetTotalAllocatedBytes(precise: true) - processBefore;
        long readBefore = GC.GetTotalAllocatedBytes(precise: true);
        long entry = (readHeap ?? LiveHeap.Read)() - small;
        long readAllocated = GC.GetTotalAllocatedBytes(precise: true) - readBefore;
        var generationsAfter = diagnose ? EntryGenerations() : default;
        if (padded) { allocated.ShouldBe(40L * 3072); }
        string generations = diagnose ? $"|gen0={generationsAfter.Gen0 - generationsBefore.Gen0}|gen1={generationsAfter.Gen1 - generationsBefore.Gen1}|gen2={generationsAfter.Gen2 - generationsBefore.Gen2}|loh={generationsAfter.Loh - generationsBefore.Loh}|poh={generationsAfter.Poh - generationsBefore.Poh}" : string.Empty;
        Console.WriteLine($"ENTRY_ALLOCATION|padded={padded}|allocated={allocated}|process_allocated={processAllocated}|read_allocated={readAllocated}|heap={entry}|extra={entry - allocated}{generations}");
        var replay = new Dictionary<object?[], object>();
        for (int i = 0; i < 1024; i++) { replay.Add(keys[i], NewHashEntry(null!, i)); }
        small = (readHeap ?? LiveHeap.Read)();
        for (int i = 1024; i < 4096; i++) { replay.Add(keys[i], NewHashEntry(null!, i)); }
        long replayBytes = (readHeap ?? LiveHeap.Read)() - small;
        var seen = new HashSet<object?[]>(); var representatives = new List<object?[]>();
        for (int i = 0; i < 1024; i++) { seen.Add(keys[i]); representatives.Add(keys[i]); }
        small = (readHeap ?? LiveHeap.Read)();
        for (int i = 1024; i < 4096; i++) { seen.Add(keys[i]); representatives.Add(keys[i]); }
        long fitting = (readHeap ?? LiveHeap.Read)() - small;
        GC.KeepAlive(keys); GC.KeepAlive(entries); GC.KeepAlive(replay); GC.KeepAlive(seen); GC.KeepAlive(representatives);
        return (entry, replayBytes, fitting);

        static object Entry(bool padded, long ordinal) => padded ? new PaddedHashEntry(null, ordinal) : NewHashEntry(null!, ordinal);
    }

    // The real entry has one state-array reference and one ordinal. One additional long
    // makes this mutant exactly 40 B; its allocation counter checks that layout every round.
    private sealed class PaddedHashEntry
    {
        internal PaddedHashEntry(object? states, long ordinal) { States = states; Ordinal = ordinal; Padding = 0; }
        internal object? States { get; }
        internal long Ordinal { get; }
        internal long Padding { get; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Gen0, long Gen1, long Gen2, long Loh, long Poh) EntryGenerations()
    {
        // Copy scalars only: the GCMemoryInfo backing object must not span a heap sample.
        var generations = GC.GetGCMemoryInfo(GCKind.FullBlocking).GenerationInfo;
        return (Live(generations[0]), Live(generations[1]), Live(generations[2]), Live(generations[3]), Live(generations[4]));

        static long Live(GCGenerationInfo generation) => generation.SizeAfterBytes - generation.FragmentationAfterBytes;
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Hash group header: dictionary growth and state-array headers fit the fixed group charge (#1789)")]
    public Task GroupHeaders_TwoPopulations_ShouldBoundMarginalHeap()
    {
        // Arrange / Act: array slots are measured with each state; isolate the array
        // header and dictionary here, with all key payloads allocated before either sample.
        MeasureGroupHeaders();
        using var pool = new HashPoolEvents();
        using var images = new WindowPoolLease();
        images.Drain(pool);

        var batch = new Batch(pool, _output, nameof(GroupHeaders_TwoPopulations_ShouldBoundMarginalHeap));
        var rounds = new List<Sample<long>>();
        for (int index = 1; index <= 3; index++)
        {
            int round = index;
            rounds.Add(batch.Measure($"round={round}", readHeap =>
            {
                images.Drain(pool);
                long measured = MeasureGroupHeaders(readHeap: readHeap);
                _output.WriteLine($"GROUP_HEADER_ROUND|round={round}|live={measured}");
                return measured;
            }));
        }
        long charged = GroupStateBytes(null!, []);
        batch.Check(() => [Ceiling("group header bytes", rounds.Min(x => x.Value) / 3072, charged, Term.Maximum(rounds) / 3072)], () =>
        {
            long live = rounds.Min(x => x.Value);
            _output.WriteLine($"GROUP_HEADER|live={live / 3072d}|charged={charged}");
            (live / 3072).ShouldBeLessThanOrEqualTo(charged);
        });
        batch.Complete();
        return Task.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureGroupHeaders(Func<long>? readHeap = null)
    {
        var keys = new object?[4096][];
        for (int i = 0; i < keys.Length; i++) { keys[i] = [i]; }
        var groups = new Dictionary<object?[], object?[]>();
        for (int i = 0; i < 1024; i++) { groups.Add(keys[i], new object?[0]); }
        long small = (readHeap ?? LiveHeap.Read)();
        for (int i = 1024; i < 4096; i++) { groups.Add(keys[i], new object?[0]); }
        long live = (readHeap ?? LiveHeap.Read)() - small;
        GC.KeepAlive(keys); GC.KeepAlive(groups);
        return live;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    [return: UnsafeAccessorType("Assimalign.Cohesion.Database.Sql.Internal.SqlPlanExecutor+HashSpillEntry, Assimalign.Cohesion.Database.Sql")]
    private static extern object NewHashEntry([UnsafeAccessorType("Assimalign.Cohesion.Database.Sql.Internal.SqlPlanExecutor+GroupAggregateState[], Assimalign.Cohesion.Database.Sql")] object states, long ordinal);
}
