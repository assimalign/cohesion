using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Testing;

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
    public void RowBytes_KeyFamilies_ShouldBoundMarginalHeap(string family)
    {
        // Arrange
        MeasureKeys(family);

        // Act
        long live = long.MaxValue, charged = 0;
        for (int round = 1; round <= 3; round++)
        {
            var measured = MeasureKeys(family);
            _output.WriteLine($"KEY_ROUND|family={family}|round={round}|live={measured.Live}|charged={measured.Charged}");
            live = Math.Min(live, measured.Live);
            charged = measured.Charged;
        }

        // Assert
        _output.WriteLine($"KEY_CHARGE|{family}|live_per_row={live / 3072d}|charged_per_row={charged / 3072d}");
        (live / 3072).ShouldBeLessThanOrEqualTo(charged / 3072);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Live, long Charged) MeasureKeys(string family)
    {
        var roots = new object?[4096][];
        SqlSelectMemory memory = default;
        long charge = 0;
        Fill(0, 1024);
        long small = LiveHeap.Read();
        Fill(1024, 4096);
        long live = LiveHeap.Read() - small;
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
    public void HashEntries_TwoPopulations_ShouldBoundMarginalHeap()
    {
        // Arrange / Act
        MeasureEntries();
        MeasureEntries(padded: true);
        long entry = long.MaxValue, replay = long.MaxValue, fitting = long.MaxValue;
        for (int round = 1; round <= 3; round++)
        {
            var measured = MeasureEntries();
            _output.WriteLine($"ENTRY_ROUND|round={round}|entry={measured.Entry}|replay={measured.Replay}|fitting={measured.Fitting}");
            entry = Math.Min(entry, measured.Entry);
            replay = Math.Min(replay, measured.Replay);
            fitting = Math.Min(fitting, measured.Fitting);

            var mutant = MeasureEntries(padded: true);
            _output.WriteLine($"ENTRY_MUTANT|round={round}|entry={mutant.Entry}|bytes_per_entry={mutant.Entry / 3072}|added_per_entry=8");
            (mutant.Entry / 3072).ShouldBeGreaterThan(32);
            Should.Throw<ShouldAssertException>(() => CheckEntryCharges(mutant.Entry, mutant.Replay, mutant.Fitting));
        }

        // Assert: +32 is the replay entry itself. RowBytes adds 32 for its container;
        // plain groups also charge 128 for their dictionary and state-array header.
        CheckEntryCharges(entry, replay, fitting);
        _output.WriteLine($"ENTRY_CHARGE|entry={entry / 3072d}|replay={replay / 3072d}|distinct={fitting / 3072d}|bound=32/96/64");
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
    private static (long Entry, long Replay, long Fitting) MeasureEntries(bool padded = false)
    {
        bool diagnose = Environment.GetEnvironmentVariable("T3_ENTRY_HEAP_DIAGNOSTICS") == "1";
        var keys = new object?[4096][]; var entries = new object?[4096];
        for (int i = 0; i < keys.Length; i++) { keys[i] = [i]; }
        for (int i = 0; i < 1024; i++) { entries[i] = Entry(padded, i); }
        long small = LiveHeap.Read();
        var generationsBefore = diagnose ? EntryGenerations() : default;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long processBefore = GC.GetTotalAllocatedBytes(precise: true);
        for (int i = 1024; i < 4096; i++) { entries[i] = Entry(padded, i); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        long processAllocated = GC.GetTotalAllocatedBytes(precise: true) - processBefore;
        long readBefore = GC.GetTotalAllocatedBytes(precise: true);
        long entry = LiveHeap.Read() - small;
        long readAllocated = GC.GetTotalAllocatedBytes(precise: true) - readBefore;
        var generationsAfter = diagnose ? EntryGenerations() : default;
        if (padded) { allocated.ShouldBe(40L * 3072); }
        string generations = diagnose ? $"|gen0={generationsAfter.Gen0 - generationsBefore.Gen0}|gen1={generationsAfter.Gen1 - generationsBefore.Gen1}|gen2={generationsAfter.Gen2 - generationsBefore.Gen2}|loh={generationsAfter.Loh - generationsBefore.Loh}|poh={generationsAfter.Poh - generationsBefore.Poh}" : string.Empty;
        Console.WriteLine($"ENTRY_ALLOCATION|padded={padded}|allocated={allocated}|process_allocated={processAllocated}|read_allocated={readAllocated}|heap={entry}|extra={entry - allocated}{generations}");
        var replay = new Dictionary<object?[], object>();
        for (int i = 0; i < 1024; i++) { replay.Add(keys[i], NewHashEntry(null!, i)); }
        small = LiveHeap.Read();
        for (int i = 1024; i < 4096; i++) { replay.Add(keys[i], NewHashEntry(null!, i)); }
        long replayBytes = LiveHeap.Read() - small;
        var seen = new HashSet<object?[]>(); var representatives = new List<object?[]>();
        for (int i = 0; i < 1024; i++) { seen.Add(keys[i]); representatives.Add(keys[i]); }
        small = LiveHeap.Read();
        for (int i = 1024; i < 4096; i++) { seen.Add(keys[i]); representatives.Add(keys[i]); }
        long fitting = LiveHeap.Read() - small;
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
    public void GroupHeaders_TwoPopulations_ShouldBoundMarginalHeap()
    {
        // Arrange / Act: array slots are measured with each state; isolate the array
        // header and dictionary here, with all key payloads allocated before either sample.
        MeasureGroupHeaders();
        long live = long.MaxValue;
        for (int round = 1; round <= 3; round++)
        {
            long measured = MeasureGroupHeaders();
            _output.WriteLine($"GROUP_HEADER_ROUND|round={round}|live={measured}");
            live = Math.Min(live, measured);
        }
        long charged = GroupStateBytes(null!, []);

        // Assert
        _output.WriteLine($"GROUP_HEADER|live={live / 3072d}|charged={charged}");
        (live / 3072).ShouldBeLessThanOrEqualTo(charged);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureGroupHeaders()
    {
        var keys = new object?[4096][];
        for (int i = 0; i < keys.Length; i++) { keys[i] = [i]; }
        var groups = new Dictionary<object?[], object?[]>();
        for (int i = 0; i < 1024; i++) { groups.Add(keys[i], new object?[0]); }
        long small = LiveHeap.Read();
        for (int i = 1024; i < 4096; i++) { groups.Add(keys[i], new object?[0]); }
        long live = LiveHeap.Read() - small;
        GC.KeepAlive(keys); GC.KeepAlive(groups);
        return live;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    [return: UnsafeAccessorType("Assimalign.Cohesion.Database.Sql.Internal.SqlPlanExecutor+HashSpillEntry, Assimalign.Cohesion.Database.Sql")]
    private static extern object NewHashEntry([UnsafeAccessorType("Assimalign.Cohesion.Database.Sql.Internal.SqlPlanExecutor+GroupAggregateState[], Assimalign.Cohesion.Database.Sql")] object states, long ordinal);
}
