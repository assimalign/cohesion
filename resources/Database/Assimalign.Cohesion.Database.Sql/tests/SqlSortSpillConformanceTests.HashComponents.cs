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
        var (live, charged) = MeasureKeys(family);

        // Assert
        _output.WriteLine($"KEY_CHARGE|{family}|live_per_row={live / 3072d}|charged_per_row={charged / 3072d}");
        live.ShouldBeLessThanOrEqualTo(charged);
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
        var (entry, replay, fitting) = MeasureEntries();

        // Assert: +32 is the replay entry itself. RowBytes adds 32 for its container;
        // plain groups also charge 128 for their dictionary and state-array header.
        entry.ShouldBeLessThanOrEqualTo(32L * 3072);
        (replay - entry).ShouldBeLessThanOrEqualTo(64L * 3072);
        replay.ShouldBeLessThanOrEqualTo((32L + 64) * 3072);
        fitting.ShouldBeLessThanOrEqualTo(64L * 3072);
        _output.WriteLine($"ENTRY_CHARGE|entry={entry / 3072d}|replay={replay / 3072d}|distinct={fitting / 3072d}|bound=32/96/64");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Entry, long Replay, long Fitting) MeasureEntries()
    {
        var keys = new object?[4096][]; var entries = new object?[4096];
        for (int i = 0; i < keys.Length; i++) { keys[i] = [i]; }
        for (int i = 0; i < 1024; i++) { entries[i] = NewHashEntry(null!, i); }
        long small = LiveHeap.Read();
        for (int i = 1024; i < 4096; i++) { entries[i] = NewHashEntry(null!, i); }
        long entry = LiveHeap.Read() - small;
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
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Hash group header: dictionary growth and state-array headers fit the fixed group charge (#1789)")]
    public void GroupHeaders_TwoPopulations_ShouldBoundMarginalHeap()
    {
        // Arrange / Act: array slots are measured with each state; isolate the array
        // header and dictionary here, with all key payloads allocated before either sample.
        MeasureGroupHeaders();
        long live = MeasureGroupHeaders();
        long charged = GroupStateBytes(null!, []);

        // Assert
        _output.WriteLine($"GROUP_HEADER|live={live / 3072d}|charged={charged}");
        live.ShouldBeLessThanOrEqualTo(3072 * charged);
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
