using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Testing;

using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapPool;
using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapAcceptance;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Live output peaks during drain and append stay bounded as row count grows (#1789)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_OutputDuringOperation_ShouldBoundLiveHeap(bool grouped, bool mutant)
    {
        // Arrange: each population uses three executions in one warmed engine. Root
        // reusable pool images before each baseline so collections cannot reclaim them mid-sample.
        long grant = grouped ? 8388608 : 1048576;
        const long writer = 26392, slack = 524288;
        var peaks = new List<long>();
        using var pool = new HashPoolEvents();
        await using var batch = new Batch(pool, _output, nameof(ExecuteAsync_OutputDuringOperation_ShouldBoundLiveHeap));
        var populations = new List<List<Sample<(long Peak, long Drain)>>>();
        var drainPeaks = new List<long>();
        foreach (int count in new[] { 4000, 16000 })
        {
            var rounds = new List<Sample<(long Peak, long Drain)>>();
            populations.Add(rounds);
            var retained = new List<object>();
            bool retaining = false;
            var builder = SqlDatabaseEngine.CreateBuilder("output-during");
            builder.Options.RootPath = Path.Combine(_root, $"output-during-{grouped}-{mutant}-{count}");
            builder.Options.StatementMemoryBudget = grant; builder.Options.StatementTempSpaceQuota = 134217728;
            builder.Options.BufferPoolCapacity = 1048576;
            builder.Functions.Add(SqlScalarFunction.Create("fresh", (long id) =>
            {
                var text = new string((char)('a' + id % 23), 2048);
                if (retaining && grouped) { retained.Add(text); }
                return text;
            }, volatility: SqlFunctionVolatility.Immutable));
            var engine = await builder.BuildAsync();
            await using var engineLease = batch.LeaseAsync(engine, rounds);
            var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
            await using var sessionLease = batch.LeaseAsync(session, rounds);
            await Seed(session, count);
            string sql = grouped ? "SELECT id,MIN(k),fresh(id) FROM t GROUP BY id" : "SELECT id,fresh(id) FROM t";
            await using (var warm = (DatabaseResultSet)await session.ExecuteAsync(sql)) { }
            var images = new WindowPoolLease();
            await using var imagesLease = batch.Lease(images, rounds);
            images.Drain(pool);
            long minimum = long.MaxValue, minimumDrain = long.MaxValue;
            for (int executionIndex = 0; executionIndex < 3; executionIndex++)
            {
                int execution = executionIndex;
                var sampled = await batch.MeasureAsync($"grouped={grouped}|mutant={mutant}|rows={count}|execution={execution}", async readHeap =>
                {
                    retained.Clear();
                    images.Drain(pool);
                    long baseline = readHeap(), peak = 0, drainPeak = 0;
                    int appended = 0, drained = 0, sampled = 0, drainSamples = 0;
                    SqlStatementMetrics? metrics = null;
                    SqlStatementMetrics.AfterMemoryReservation = (current, _) => metrics = current;
                    SqlRowCollector.ObserveBuffer = (phase, row) =>
                    {
                        if (phase == "output-drain" && retaining && !grouped)
                        { retained.Add(((object?[])row).Clone()); }
                    };
                    retaining = mutant;
                    SqlRowCollector.ObserveSpill = phase =>
                    {
                        if (phase == "output-drained") { Sample(true); drained++; if (!grouped) { retained.Clear(); } }
                        if (phase == "output-append" && ++appended % (drained == 0 ? 16 : 512) == 0)
                        { Sample(drained == 0); }
                    };
                    try
                    {
                        // Act: the root mutant copies drained rows until the drain ends.
                        // The grouped mutant retains every projected payload until completion.
                        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql);
                        // Assert
                        drained.ShouldBe(1); appended.ShouldBe(count); sampled.ShouldBeGreaterThan(1);
                        drainSamples.ShouldBeGreaterThan(1);

                        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
                        SqlSortSpillMetrics.For(session.LastStatementMetrics).ShouldNotBeNull().Runs.ShouldBe(1);
                        GC.KeepAlive(retained);
                        return (Peak: peak, Drain: drainPeak);
                    }
                    finally
                    {
                        SqlRowCollector.ObserveSpill = null; SqlRowCollector.ObserveBuffer = null;
                        SqlStatementMetrics.AfterMemoryReservation = null; retaining = false;
                    }
                    void Sample(bool duringDrain)
                    {
                        sampled++;
                        long live = readHeap() - baseline;
                        peak = Math.Max(peak, live);
                        if (duringDrain)
                        {
                            drainSamples++;
                            drainPeak = Math.Max(drainPeak, live - (metrics?.HashMemoryBytes ?? 0));
                        }
                    }
                });
                rounds.Add(sampled);
                await batch.CheckAsync(() => [Floor("output execution peak > 0", sampled.Value.Peak, 0, sampled.Term, true)],
                    () => sampled.Value.Peak.ShouldBeGreaterThan(0));
                minimum = Math.Min(minimum, sampled.Value.Peak);
                minimumDrain = Math.Min(minimumDrain, sampled.Value.Drain);
            }
            peaks.Add(minimum); drainPeaks.Add(minimumDrain);
            await batch.CheckAsync(() =>
            {
                long peak = rounds.Min(x => x.Value.Peak), drain = rounds.Min(x => x.Value.Drain);
                Term term = Term.Maximum(rounds);
                return mutant
                    ? [Floor("output mutant violation", Math.Max(peak - grant - writer - slack, drain - writer - slack), 0, term, true)]
                    : [Ceiling("output peak", peak, grant + writer + slack, term), Ceiling("output drain excess", drain, writer + slack, term)];
            }, () =>
            {
                minimum = rounds.Min(x => x.Value.Peak); minimumDrain = rounds.Min(x => x.Value.Drain);
            _output.WriteLine($"OUTPUT_DURING|grouped={grouped}|mutant={mutant}|rows={count}|peak={minimum}|grant={grant}|writer={writer}|slack={slack}|drainExcess={minimumDrain}");
            void AssertBound()
            {
                minimum.ShouldBeLessThanOrEqualTo(grant + writer + slack);
                minimumDrain.ShouldBeLessThanOrEqualTo(writer + slack);
            }
            if (mutant) { Should.Throw<ShouldAssertException>(AssertBound); }
            else { AssertBound(); }
            });
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        if (!mutant)
        {
            await batch.CheckAsync(() => [Ceiling("output population peak growth", populations[1].Min(x => x.Value.Peak) - populations[0].Min(x => x.Value.Peak), slack,
                Term.Maximum(populations[1]) + Term.Maximum(populations[0])),
                Ceiling("output population drain growth", populations[1].Min(x => x.Value.Drain) - populations[0].Min(x => x.Value.Drain), slack,
                Term.Maximum(populations[1]) + Term.Maximum(populations[0]))], () =>
            {
                for (int population = 0; population < 2; population++)
                { peaks[population] = populations[population].Min(x => x.Value.Peak); drainPeaks[population] = populations[population].Min(x => x.Value.Drain); }
                (peaks[1] - peaks[0]).ShouldBeLessThanOrEqualTo(slack);
                (drainPeaks[1] - drainPeaks[0]).ShouldBeLessThanOrEqualTo(slack);
            });
        }
        await batch.CompleteAsync();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Live copy-credit peaks stay bounded as row count grows (#1789, #1838)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CopyCreditDuringOperation_ShouldBoundLiveHeap(bool mutant)
    {
        // Arrange: The NULL source fills G exactly, so no list or record reservation can succeed.
        const long batch = 520472, slack = 32768;
        using var pool = new HashPoolEvents();
        using var images = new WindowPoolLease();
        using var copyImages = new CopyPoolLease();
        var excesses = new List<long>();
        var copyPeaks = new List<long>();
        var acceptance = new Batch(pool, _output, nameof(ExecuteAsync_CopyCreditDuringOperation_ShouldBoundLiveHeap));
        var populations = new List<List<Sample<(long Peak, long Copy)>>>();
        foreach (int count in new[] { 10000, 100000 })
        {
            long grant = 64L * count;
            await using var fixture = await BuildFixture(count, "");
            var engine = fixture.Engine; var session = fixture.Session;
            var rounds = new List<Sample<(long Peak, long Copy)>>();
            populations.Add(rounds);
            images.Drain(pool);

            var retained = new List<byte[]>();
            long minimum = long.MaxValue, minimumCopy = long.MaxValue;
            for (int executionIndex = 0; executionIndex < 3; executionIndex++)
            {
                int execution = executionIndex, measurement = 0;
                var sampled = await acceptance.MeasureAsync($"mutant={mutant}|rows={count}|execution={execution}", async readHeap =>
                {
                    retained.Clear();
                    int attempt = measurement++;
                    await using var retry = attempt == 0 ? null : await BuildFixture(count, $"-execution-{execution}-attempt-{attempt}");
                    var targetSession = retry?.Session ?? session; var targetEngine = retry?.Engine ?? engine;
                    if (retry is not null)
                    {
                        for (int prior = 0; prior < execution; prior++)
                        { (await targetSession.ExecuteAsync(string.Concat("CREATE TABLE archive", prior.ToString(), " AS SELECT note FROM t"))).AffectedCount.ShouldBe(count); }
                    }
                    images.Drain(pool); copyImages.Prepare();
                    long baseline = readHeap(), copyBaseline = 0, peak = 0, copyPeak = 0, admitted = 0, smallest = long.MaxValue;
                    int encoded = 0, sampled = 0;
                    SqlStatementMetrics? metrics = null;
                    SqlStatementMetrics.AfterMemoryReservation = (current, _) => metrics = current;
                    SqlPlanExecutor.TableAsSourceAdmitted = bytes => { admitted = bytes; copyImages.Drain(pool); copyBaseline = readHeap(); };
                    SqlPlanExecutor.TableAsRecordEncoded = record =>
                    {
                        // Models a copy implementation that lets uncharged batches grow
                        // past 64 records instead of releasing them at each flush.
                        if (mutant) { retained.Add(record); }
                        if (++encoded % 512 == 0)
                        {
                            sampled++; long live = readHeap();
                            peak = Math.Max(peak, live - baseline); copyPeak = Math.Max(copyPeak, live - copyBaseline);
                        }
                    };
                    SqlPlanExecutor.TableAsStage = (phase, _) =>
                    { if (phase == "Copied") { smallest = Math.Min(smallest, metrics!.HashMemoryBytes); } };
                    try
                    {
                        // Act
                        (await targetSession.ExecuteAsync(string.Concat("CREATE TABLE archive", execution.ToString(), " AS SELECT note FROM t"))).AffectedCount.ShouldBe(count);
                        // Assert: completing with the source at G and no temp run proves that
                        // the fixed copy-refusal allowance ran; NULL cells release no payload.
                        admitted.ShouldBe(64L * count); smallest.ShouldBe(admitted);
                        encoded.ShouldBe(count); sampled.ShouldBeGreaterThan(1);
                        TableAsPoolCount(TableAsPool(targetSession.Database.DataStorage)).ShouldBe(128);
                        targetSession.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
                        SqlSortSpillMetrics.For(targetSession.LastStatementMetrics).ShouldBeNull();

                        GC.KeepAlive(retained);
                        return (Peak: peak, Copy: copyPeak);
                    }
                    finally
                    {
                        SqlStatementMetrics.AfterMemoryReservation = null; SqlPlanExecutor.TableAsSourceAdmitted = null;
                        SqlPlanExecutor.TableAsRecordEncoded = null; SqlPlanExecutor.TableAsStage = null;
                    }
                });
                rounds.Add(sampled);
                minimum = Math.Min(minimum, sampled.Value.Peak); minimumCopy = Math.Min(minimumCopy, sampled.Value.Copy);
            }
            long excess = minimum - grant - batch - slack; excesses.Add(excess); copyPeaks.Add(minimumCopy);
            await acceptance.CheckAsync(() => mutant ? [] : [Ceiling("copy credit peak", rounds.Min(x => x.Value.Peak), grant + batch + slack, Term.Maximum(rounds))], () =>
            {
                minimum = rounds.Min(x => x.Value.Peak); minimumCopy = rounds.Min(x => x.Value.Copy);
                excess = minimum - grant - batch - slack;
                _output.WriteLine($"COPY_CREDIT_DURING|mutant={mutant}|rows={count}|peak={minimum}|grant={grant}|batch={batch}|slack={slack}|copyPeak={minimumCopy}");
                if (!mutant) { excess.ShouldBeLessThanOrEqualTo(0); }
            });
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        await acceptance.CheckAsync(() =>
        {
            long growth = populations[1].Min(x => x.Value.Copy) - populations[0].Min(x => x.Value.Copy);
            Term term = Term.Maximum(populations[1]) + Term.Maximum(populations[0]);
            long excess = Math.Max(populations[0].Min(x => x.Value.Peak) - 64L * 10000 - batch - slack,
                populations[1].Min(x => x.Value.Peak) - 64L * 100000 - batch - slack);
            return mutant ? [Floor("copy mutant absolute growth", Math.Abs(growth), 524288, term, true),
                    Floor("copy mutant peak violation", excess, 0, Term.Maximum(populations.SelectMany(x => x)), true)]
                : [Ceiling("copy absolute population growth", Math.Abs(growth), 524288, term)];
        }, () =>
        {
            for (int population = 0; population < 2; population++)
            {
                copyPeaks[population] = populations[population].Min(x => x.Value.Copy);
                excesses[population] = populations[population].Min(x => x.Value.Peak) - 64L * (population == 0 ? 10000 : 100000) - batch - slack;
            }
        long growth = copyPeaks[1] - copyPeaks[0];
        _output.WriteLine($"COPY_CREDIT_GROWTH|mutant={mutant}|growth={growth}|bound=524288");
        void AssertGrowth() => Math.Abs(growth).ShouldBeLessThanOrEqualTo(524288);
        if (mutant)
        {
            Should.Throw<ShouldAssertException>(AssertGrowth);
            Should.Throw<ShouldAssertException>(() => excesses.Max().ShouldBeLessThanOrEqualTo(0));
        }
        else { AssertGrowth(); }
        });
        await acceptance.CompleteAsync();

        async Task<CopyCreditFixture> BuildFixture(int count, string suffix)
        {
            long grant = 64L * count;
            var builder = SqlDatabaseEngine.CreateBuilder("credit-during");
            builder.Options.RootPath = Path.Combine(_root, $"credit-during-{mutant}-{count}{suffix}");
            builder.Options.StatementMemoryBudget = grant; builder.Options.BufferPoolCapacity = 1048576;
            var engine = await builder.BuildAsync();
            SqlDatabaseSession? session = null;
            try
            {
            session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
            await session.ExecuteAsync("CREATE TABLE t(id INT,note TEXT)");
            for (int first = 0; first < count; first += 200)
            { await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(first, Math.Min(200,count-first)).Select(i => $"({i},NULL)"))); }
            await session.ExecuteAsync("CREATE TABLE warm AS SELECT note FROM t");
            await session.ExecuteAsync("CREATE TABLE pool_fill(note TEXT)");
            for (int first = 0; first < 1000; first += 25)
            { await session.ExecuteAsync("INSERT INTO pool_fill VALUES" + string.Join(',', Enumerable.Range(0, 25).Select(_ => "('" + new string('x', 2048) + "')"))); }
            // Keep the warm owners alive so reclamation cannot evict frames during a sample.
            for (int pass = 0; pass < 8 && TableAsPoolCount(TableAsPool(session.Database.DataStorage)) < 128; pass++)
            {
                await using var scan = (DatabaseResultSet)await session.ExecuteAsync("SELECT MAX(note) FROM pool_fill");
                (await Rows(scan)).ShouldHaveSingleItem().Cells[0].ShouldBe(new string('x', 2048));
            }
            TableAsPoolCount(TableAsPool(session.Database.DataStorage)).ShouldBe(128);

            return new CopyCreditFixture(engine, session);
            }
            catch
            {
                try { if (session is not null) { await session.DisposeAsync(); } }
                finally { await engine.DisposeAsync(); }
                throw;
            }
        }
    }

    private sealed class CopyPoolLease : IDisposable
    {
        private readonly List<byte[]> _buffers = [];

        // Reserve bookkeeping before the outer baseline. Only existing pooled images
        // may survive the inner baseline; fresh drain probes are fixture allocations.
        internal void Prepare() => _buffers.EnsureCapacity(_buffers.Count + 8 * 1024);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Drain(HashPoolEvents events)
        {
            foreach (int size in new[] { 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576 })
            {
                long allocated = events.Allocated;
                int rents = 0;
                do
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(size); rents++;
                    if (events.Allocated != allocated) { break; }
                    _buffers.Add(buffer);
                }
                while (rents < 1024);
                events.Allocated.ShouldBeGreaterThan(allocated);
            }
            // The returned frame releases each fresh probe before LiveHeap.Read.
        }

        public void Dispose()
        { foreach (var buffer in _buffers) { ArrayPool<byte>.Shared.Return(buffer); } _buffers.Clear(); }
    }
    private sealed record CopyCreditFixture(SqlDatabaseEngine Engine, SqlDatabaseSession Session) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        { try { await Session.DisposeAsync(); } finally { await Engine.DisposeAsync(); } }
    }

}
