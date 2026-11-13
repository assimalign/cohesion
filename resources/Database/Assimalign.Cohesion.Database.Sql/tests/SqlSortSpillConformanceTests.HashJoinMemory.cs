using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Hash join entries and match flags bound marginal live heap for codec families (#1789)")]
    [InlineData("INT")]
    [InlineData("BIGINT")]
    [InlineData("NUMERIC(12,2)")]
    [InlineData("REAL")]
    [InlineData("DOUBLE PRECISION")]
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
    public void HashJoinEntries_TwoPopulations_ShouldBoundLiveHeap(string family)
    {
        // Arrange / Act
        MeasureJoinEntries(family);
        long live = long.MaxValue, charge = 0;
        for (int round = 1; round <= 3; round++)
        {
            var measured = MeasureJoinEntries(family);
            _output.WriteLine($"JOIN_ENTRY_ROUND|family={family}|round={round}|live={measured.Live}|charged={measured.Charge}");
            live = Math.Min(live, measured.Live);
            charge = measured.Charge;
        }
        // Assert
        live.ShouldBeLessThanOrEqualTo(charge);
        _output.WriteLine($"JOIN_ENTRY_CHARGE|family={family}|live={live}|charged={charge}|population_delta=12288");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Live, long Charge) MeasureJoinEntries(string family)
    {
        var metrics = new SqlStatementMetrics();
        using var table = new SqlHashJoinTable(new([0], [0], [Collation.Binary]), metrics, long.MaxValue, trackUnmatched: true);
        // A larger marginal population dominates process-wide runner/diagnostic roots.
        // There is still no byte tolerance: the unchanged charge must bound the heap.
        Fill(0, 4096);
        long baseline = LiveHeap.Read(), charge = metrics.HashMemoryBytes;
        Fill(4096, 16384);
        long live = LiveHeap.Read() - baseline;
        charge = metrics.HashMemoryBytes - charge;
        GC.KeepAlive(table);
        return (live, charge);

        void Fill(int first, int last)
        {
            for (int i=first; i<last; i++)
            {
                object? value = family switch
                {
                    "TEXT" => new string('x',128), "BINARY" => new byte[256],
                    "CHAR" => new SqlCharacter(new string('x',128)+"   "), _ => StateValue(family,i),
                };
                table.Add([i,value], CancellationToken.None);
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash join replay live objects stay bounded at two row counts and reject a retaining mutant (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HashJoinReplay_TwoPopulations_ShouldRejectRetainingMutant(bool full)
    {
        // Arrange / Act
        await using var engine = await Create("join-live", 1048576);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        MeasureJoinReplay(session, 3000, mutant: false, sample: false, full: full);
        var small = MeasurePopulation(6000);
        var large = MeasurePopulation(12000);
        var retainedSmall = MeasureJoinReplay(session, 6000, mutant: true, sample: true, full: full);
        var retainedLarge = MeasureJoinReplay(session, 12000, mutant: true, sample: true, full: full);
        // Assert
        small.Peak.ShouldBeLessThanOrEqualTo(1048576+262144); large.Peak.ShouldBeLessThanOrEqualTo(1048576+262144);
        (large.Peak-small.Peak).ShouldBeLessThanOrEqualTo(131072);
        (large.Replay-small.Replay).ShouldBeLessThanOrEqualTo(131072);
        (retainedLarge.Replay-retainedSmall.Replay).ShouldBeGreaterThan(131072);
        _output.WriteLine($"JOIN_LIVE|full={full}|small={small.Peak}|large={large.Peak}|delta={large.Peak-small.Peak}|replay_delta={large.Replay-small.Replay}|mutant_replay_delta={retainedLarge.Replay-retainedSmall.Replay}");
        TempBytes(engine.TempSpace!).ShouldBe(0); Statements(engine.TempSpace!).ShouldBeEmpty(); Files(engine).ShouldBe(0);

        (long Peak, long Replay) MeasurePopulation(int count)
        {
            long peak = long.MaxValue, replay = long.MaxValue;
            for (int execution = 1; execution <= 3; execution++)
            {
                var measured = MeasureJoinReplay(session, count, mutant: false, sample: true, full: full, execution: execution);
                measured.Peak.ShouldBeLessThanOrEqualTo(1048576 + 262144);
                peak = Math.Min(peak, measured.Peak);
                replay = Math.Min(replay, measured.Replay);
            }
            return (peak, replay);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Seeded hash join replay corpus preserves duplicate order and decimal bits at 1, 4 and 16 MiB (#1789)")]
    [InlineData(1048576)]
    [InlineData(4194304)]
    [InlineData(16777216)]
    public async Task HashJoinReplay_SeededGrantCorpus_ShouldPreserveEveryPairAndFlag(long grant)
    {
        // Arrange
        const int count = 12000;
        await using var engine = await Create("join-corpus", grant);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        var keys = new SqlHashJoinPath([0],[0],[Collation.Binary]);
        var metrics = new SqlStatementMetrics();
        using var reference = new SqlHashJoinTable(keys,new SqlStatementMetrics(),long.MaxValue,trackUnmatched:true);
        using var actual = new SqlHashJoinTable(keys,metrics,grant,trackUnmatched:true);
        var random = new Random(178906);
        int[] probe = Enumerable.Range(0,128).Select(_ => random.Next(907)).Distinct().ToArray();
        try
        {
            for (int i=0; i<count; i++)
            {
                object?[] row = [i%907,i,new string((char)('a'+random.Next(26)),1280),new decimal(i,0,0,false,(byte)(i%3))];
                reference.Add(row,CancellationToken.None);
                try { actual.Add(row,CancellationToken.None); }
                catch (SqlExecutionException error) when (error.Diagnostics[0].Code == "CS05067")
                { actual.Spill(session,row,CancellationToken.None); }
            }
            // Act / Assert: every candidate, duplicate order and complete unmatched suffix.
            foreach (int key in probe)
            {
                var expected = reference.ProbeEntries([key],CancellationToken.None).ToArray();
                var found = actual.ProbeEntries([key],CancellationToken.None).ToArray();
                found.Length.ShouldBe(expected.Length);
                for (int i=0; i<found.Length; i++)
                {
                    new RowValues(actual.RowAt(found[i])).ShouldBe(new RowValues(reference.RowAt(expected[i])));
                    reference.MarkMatched(expected[i]); actual.MarkMatched(found[i]);
                }
            }
            var expectedRows = reference.Unmatched(CancellationToken.None).Select(r => new RowValues(r)).ToArray();
            actual.Unmatched(CancellationToken.None).Select(r => new RowValues(r)).ToArray().ShouldBe(expectedRows);
            metrics.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(grant);
            SqlHashSpillMetrics.For(metrics).ShouldNotBeNull().Runs.ShouldBeGreaterThan(0);
            _output.WriteLine($"JOIN_CORPUS|seed=178906|grant={grant}|rows={count}|probes={probe.Length}|unmatched={expectedRows.Length}");
        }
        finally { actual.Dispose(); SqlHashSpillMetrics.DetachJoinTemp(metrics)?.Dispose(); }
        metrics.HashMemoryBytes.ShouldBe(0); TempBytes(engine.TempSpace!).ShouldBe(0); Statements(engine.TempSpace!).ShouldBeEmpty();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private (long Peak, long Replay) MeasureJoinReplay(SqlDatabaseSession session, int count, bool mutant, bool sample, bool full, int execution = 0)
    {
        var retained = new List<object>();
        long baseline = LiveHeap.Read(), peak = 0, replay = 0;
        int replaySamples = 0, unmatchedSamples = 0, callbacks = 0;
        SqlPlanExecutor.ObserveHashSpill = (phase, ordinal) =>
        {
            if (!sample) { return; }
            if (phase == "join-batch-live") { replaySamples++; }
            if (phase == "join-unmatched-live") { unmatchedSamples++; }
            if (phase == "join-input" && ordinal%1000==0 || phase == "join-batch-live" && ++callbacks%16==0 || phase is "join-partitioned" or "join-match-sort-live" or "join-unmatched-live")
            {
                long live = LiveHeap.Read();
                peak = Math.Max(peak, live);
                if (phase != "join-input") { replay = Math.Max(replay, live); }
            }
        };
        var metrics = new SqlStatementMetrics();
        using var table = new SqlHashJoinTable(new([0], [0], [Collation.Binary]), metrics, 1048576, trackUnmatched: full);
        try
        {
            for (int i=0; i<count; i++)
            {
                object?[] row = [i,new string('x',128)];
                try { table.Add(row,CancellationToken.None); }
                catch (SqlExecutionException error) when (error.Diagnostics[0].Code == "CS05067")
                { table.Spill(session,row,CancellationToken.None); }
                if (mutant) { retained.Add(new byte[32]); }
            }
            for (int i=0; i<128; i++)
            {
                if (full)
                {
                    foreach (int entry in table.ProbeEntries([i*(count/128)],CancellationToken.None))
                    { table.RowAt(entry)[0].ShouldBe(i*(count/128)); table.MarkMatched(entry); }
                }
                else { foreach (var row in table.Probe([i*(count/128)],CancellationToken.None)) { row[0].ShouldBe(i*(count/128)); } }
            }
            if (full)
            {
                int remaining = 0;
                foreach (var row in table.Unmatched(CancellationToken.None)) { row[0].ShouldNotBeNull(); remaining++; }
                remaining.ShouldBe(count-128);
                if (sample) { unmatchedSamples.ShouldBeGreaterThan(0); }
            }
            if (sample) { replaySamples.ShouldBeGreaterThan(0); }
            metrics.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(1048576);
            GC.KeepAlive(retained);
        }
        finally
        {
            table.Dispose(); SqlHashSpillMetrics.DetachJoinTemp(metrics)?.Dispose(); SqlPlanExecutor.ObserveHashSpill = null;
            metrics.HashMemoryBytes.ShouldBe(0);
        }
        long after = LiveHeap.Read();
        peak = Math.Max(0, peak - baseline);
        replay = Math.Max(0, replay - Math.Min(baseline, after));
        if (sample)
        { _output.WriteLine($"JOIN_REPLAY_EXECUTION|rows={count}|execution={execution}|full={full}|mutant={mutant}|before={baseline}|after={after}|peak={peak}|replay={replay}"); }
        GC.KeepAlive(retained);
        return (peak, replay);
    }
}
