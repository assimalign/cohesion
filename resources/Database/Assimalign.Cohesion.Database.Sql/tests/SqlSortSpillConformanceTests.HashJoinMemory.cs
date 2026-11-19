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

using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapPool;
using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapAcceptance;

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
    public Task HashJoinEntries_TwoPopulations_ShouldBoundLiveHeap(string family)
    {
        // Arrange / Act
        MeasureJoinEntries(family);
        using var pool = new HashPoolEvents();
        using var images = new WindowPoolLease();
        images.Drain(pool);

        var batch = new Batch(pool, _output, nameof(HashJoinEntries_TwoPopulations_ShouldBoundLiveHeap));
        var rounds = new List<Sample<(long Live, long Charge)>>();
        for (int index = 1; index <= 3; index++)
        {
            int round = index;
            rounds.Add(batch.Measure($"family={family}|round={round}", readHeap =>
            {
                images.Drain(pool);
                var measured = MeasureJoinEntries(family, readHeap);
                _output.WriteLine($"JOIN_ENTRY_ROUND|family={family}|round={round}|live={measured.Live}|charged={measured.Charge}");
                return measured;
            }));
        }
        batch.Check(() => [Ceiling("join entry bytes", rounds.Min(x => x.Value.Live) / 12288,
            rounds[^1].Value.Charge / 12288, Term.Maximum(rounds) / 12288)], () =>
        {
            long live = rounds.Min(x => x.Value.Live), charge = rounds[^1].Value.Charge;
            (live / 12288).ShouldBeLessThanOrEqualTo(charge / 12288);
            _output.WriteLine($"JOIN_ENTRY_CHARGE|family={family}|live={live}|charged={charge}|population_delta=12288");
        });
        batch.Complete();
        return Task.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Live, long Charge) MeasureJoinEntries(string family, Func<long>? readHeap = null)
    {
        var metrics = new SqlStatementMetrics();
        using var table = new SqlHashJoinTable(new([0], [0], [Collation.Binary]), metrics, long.MaxValue, trackUnmatched: true);
        // A larger marginal population dominates process-wide runner/diagnostic roots.
        // Compare marginal bytes per entry against the unchanged per-entry charge.
        Fill(0, 4096);
        long baseline = (readHeap ?? LiveHeap.Read)(), charge = metrics.HashMemoryBytes;
        Fill(4096, 16384);
        long live = (readHeap ?? LiveHeap.Read)() - baseline;
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
        // Keep pre-existing pooled images rooted across every baseline and sample.
        using var pool = new HashPoolEvents();
        using var images = new WindowPoolLease();
        images.Drain(pool);

        var batch = new Batch(pool, _output, nameof(HashJoinReplay_TwoPopulations_ShouldRejectRetainingMutant));
        var small = MeasurePopulation(6000);
        var large = MeasurePopulation(12000);
        var retainedSmall = MeasureExecution(6000, true, 0);
        var retainedLarge = MeasureExecution(12000, true, 0);
        long Peak(Sample<(long Peak, long Replay)>[] population) => population.Min(x => x.Value.Peak);
        long Replay(Sample<(long Peak, long Replay)>[] population) => population.Min(x => x.Value.Replay);
        // Assert
        batch.Check(() =>
        {
            Term smallTerm = Term.Maximum(small), largeTerm = Term.Maximum(large);
            return[Ceiling("small peak", Peak(small), 1048576 + 262144, smallTerm), Ceiling("large peak", Peak(large), 1048576 + 262144, largeTerm), Ceiling("population peak growth", Peak(large) - Peak(small), 131072, largeTerm + smallTerm), Ceiling("population replay growth", Replay(large) - Replay(small), 131072, largeTerm + smallTerm), Floor("retaining mutant replay growth", retainedLarge.Value.Replay - retainedSmall.Value.Replay, 131072, retainedLarge.Term + retainedSmall.Term, true)];
        }, () =>
        {
            var smallValue = (Peak: Peak(small), Replay: Replay(small));
            var largeValue = (Peak: Peak(large), Replay: Replay(large));
            smallValue.Peak.ShouldBeLessThanOrEqualTo(1048576 + 262144);
            largeValue.Peak.ShouldBeLessThanOrEqualTo(1048576 + 262144);
            (largeValue.Peak - smallValue.Peak).ShouldBeLessThanOrEqualTo(131072);
            (largeValue.Replay - smallValue.Replay).ShouldBeLessThanOrEqualTo(131072);
            (retainedLarge.Value.Replay - retainedSmall.Value.Replay).ShouldBeGreaterThan(131072);
            _output.WriteLine($"JOIN_LIVE|full={full}|small={smallValue.Peak}|large={largeValue.Peak}|delta={largeValue.Peak - smallValue.Peak}|replay_delta={largeValue.Replay - smallValue.Replay}|mutant_replay_delta={retainedLarge.Value.Replay - retainedSmall.Value.Replay}");
        });
        batch.Complete();
        TempBytes(engine.TempSpace!).ShouldBe(0); Statements(engine.TempSpace!).ShouldBeEmpty(); Files(engine).ShouldBe(0);

        Sample<(long Peak, long Replay)> MeasureExecution(int count, bool mutant, int execution) =>
            batch.Measure($"rows={count}|execution={execution}|full={full}|mutant={mutant}", readHeap =>
            {
                images.Drain(pool);
                return MeasureJoinReplay(session, count, mutant, sample: true, full: full, execution: execution, readHeap: readHeap);
            });

        Sample<(long Peak, long Replay)>[] MeasurePopulation(int count)
        {
            var rounds = new Sample<(long Peak, long Replay)>[3];
            for (int execution = 1; execution <= 3; execution++)
            {
                var measured = MeasureExecution(count, false, execution);
                rounds[execution - 1] = measured;
                batch.Check(() => [Ceiling("execution peak", measured.Value.Peak, 1048576 + 262144, measured.Term)],
                    () => measured.Value.Peak.ShouldBeLessThanOrEqualTo(1048576 + 262144));
            }
            return rounds;
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
    private (long Peak, long Replay) MeasureJoinReplay(SqlDatabaseSession session, int count, bool mutant, bool sample, bool full, int execution = 0, Func<long>? readHeap = null)
    {
        var retained = new List<object>();
        long baseline = (readHeap ?? LiveHeap.Read)(), peak = 0, replay = 0;
        int replaySamples = 0, unmatchedSamples = 0, callbacks = 0;
        SqlPlanExecutor.ObserveHashSpill = (phase, ordinal) =>
        {
            if (!sample) { return; }
            if (phase == "join-batch-live") { replaySamples++; }
            if (phase == "join-unmatched-live") { unmatchedSamples++; }
            if (phase == "join-input" && ordinal%1000==0 || phase == "join-batch-live" && ++callbacks%16==0 || phase is "join-partitioned" or "join-match-sort-live" or "join-unmatched-live")
            {
                long live = (readHeap ?? LiveHeap.Read)();
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
        long after = (readHeap ?? LiveHeap.Read)();
        peak = Math.Max(0, peak - baseline);
        replay = Math.Max(0, replay - Math.Min(baseline, after));
        if (sample)
        { _output.WriteLine($"JOIN_REPLAY_EXECUTION|rows={count}|execution={execution}|full={full}|mutant={mutant}|before={baseline}|after={after}|peak={peak}|replay={replay}"); }
        GC.KeepAlive(retained);
        return (peak, replay);
    }
}
