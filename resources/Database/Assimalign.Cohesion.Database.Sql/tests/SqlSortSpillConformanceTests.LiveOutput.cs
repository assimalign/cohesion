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
        var drainPeaks = new List<long>();
        foreach (int count in new[] { 4000, 16000 })
        {
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
            await using var engine = await builder.BuildAsync();
            await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session, count);
            string sql = grouped ? "SELECT id,MIN(k),fresh(id) FROM t GROUP BY id" : "SELECT id,fresh(id) FROM t";
            await using (var warm = (DatabaseResultSet)await session.ExecuteAsync(sql)) { }
            using var pool = new HashPoolEvents();
            using var images = new WindowPoolLease();
            images.Drain(pool); await QuiesceWindowPoolAsync(pool);
            long minimum = long.MaxValue, minimumDrain = long.MaxValue;
            for (int execution = 0; execution < 3; execution++)
            {
                retained.Clear();
                images.Drain(pool);
                long baseline = LiveHeap.Read(), peak = 0, drainPeak = 0;
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
                    peak.ShouldBeGreaterThan(0);
                    minimum = Math.Min(minimum, peak);
                    minimumDrain = Math.Min(minimumDrain, drainPeak);
                    session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
                    SqlSortSpillMetrics.For(session.LastStatementMetrics).ShouldNotBeNull().Runs.ShouldBe(1);
                    GC.KeepAlive(retained);
                }
                finally
                {
                    SqlRowCollector.ObserveSpill = null; SqlRowCollector.ObserveBuffer = null;
                    SqlStatementMetrics.AfterMemoryReservation = null; retaining = false;
                }
                void Sample(bool duringDrain)
                {
                    sampled++;
                    long live = LiveHeap.Read() - baseline;
                    peak = Math.Max(peak, live);
                    if (duringDrain)
                    {
                        drainSamples++;
                        drainPeak = Math.Max(drainPeak, live - (metrics?.HashMemoryBytes ?? 0));
                    }
                }
            }
            peaks.Add(minimum); drainPeaks.Add(minimumDrain);
            _output.WriteLine($"OUTPUT_DURING|grouped={grouped}|mutant={mutant}|rows={count}|peak={minimum}|grant={grant}|writer={writer}|slack={slack}|drainExcess={minimumDrain}");
            void AssertBound()
            {
                minimum.ShouldBeLessThanOrEqualTo(grant + writer + slack);
                minimumDrain.ShouldBeLessThanOrEqualTo(writer + slack);
            }
            if (mutant) { Should.Throw<ShouldAssertException>(AssertBound); }
            else { AssertBound(); }
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        if (!mutant)
        {
            (peaks[1] - peaks[0]).ShouldBeLessThanOrEqualTo(slack);
            (drainPeaks[1] - drainPeaks[0]).ShouldBeLessThanOrEqualTo(slack);
        }
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
        foreach (int count in new[] { 10000, 100000 })
        {
            long grant = 64L * count;
            var builder = SqlDatabaseEngine.CreateBuilder("credit-during");
            builder.Options.RootPath = Path.Combine(_root, $"credit-during-{mutant}-{count}");
            builder.Options.StatementMemoryBudget = grant; builder.Options.BufferPoolCapacity = 1048576;
            await using var engine = await builder.BuildAsync();
            await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
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
            images.Drain(pool);
            if (count == 10000) { await QuiesceWindowPoolAsync(pool); }
            var retained = new List<byte[]>();
            long minimum = long.MaxValue, minimumCopy = long.MaxValue;
            for (int execution = 0; execution < 3; execution++)
            {
                retained.Clear();
                images.Drain(pool); copyImages.Prepare();
                long baseline = LiveHeap.Read(), copyBaseline = 0, peak = 0, copyPeak = 0, admitted = 0, smallest = long.MaxValue;
                int encoded = 0, sampled = 0;
                SqlStatementMetrics? metrics = null;
                SqlStatementMetrics.AfterMemoryReservation = (current, _) => metrics = current;
                SqlPlanExecutor.TableAsSourceAdmitted = bytes => { admitted = bytes; copyImages.Drain(pool); copyBaseline = LiveHeap.Read(); };
                SqlPlanExecutor.TableAsRecordEncoded = record =>
                {
                    // Models a copy implementation that lets uncharged batches grow
                    // past 64 records instead of releasing them at each flush.
                    if (mutant) { retained.Add(record); }
                    if (++encoded % 512 == 0)
                    {
                        sampled++; long live = LiveHeap.Read();
                        peak = Math.Max(peak, live - baseline); copyPeak = Math.Max(copyPeak, live - copyBaseline);
                    }
                };
                SqlPlanExecutor.TableAsStage = (phase, _) =>
                { if (phase == "Copied") { smallest = Math.Min(smallest, metrics!.HashMemoryBytes); } };
                try
                {
                    // Act
                    (await session.ExecuteAsync($"CREATE TABLE archive{execution} AS SELECT note FROM t")).AffectedCount.ShouldBe(count);
                    // Assert: completing with the source at G and no temp run proves that
                    // the fixed copy-refusal allowance ran; NULL cells release no payload.
                    admitted.ShouldBe(64L * count); smallest.ShouldBe(admitted);
                    encoded.ShouldBe(count); sampled.ShouldBeGreaterThan(1);
                    TableAsPoolCount(TableAsPool(session.Database.DataStorage)).ShouldBe(128);
                    session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
                    SqlSortSpillMetrics.For(session.LastStatementMetrics).ShouldBeNull();
                    minimum = Math.Min(minimum, peak); minimumCopy = Math.Min(minimumCopy, copyPeak);
                    GC.KeepAlive(retained);
                }
                finally
                {
                    SqlStatementMetrics.AfterMemoryReservation = null; SqlPlanExecutor.TableAsSourceAdmitted = null;
                    SqlPlanExecutor.TableAsRecordEncoded = null; SqlPlanExecutor.TableAsStage = null;
                }
            }
            long excess = minimum - grant - batch - slack; excesses.Add(excess); copyPeaks.Add(minimumCopy);
            _output.WriteLine($"COPY_CREDIT_DURING|mutant={mutant}|rows={count}|peak={minimum}|grant={grant}|batch={batch}|slack={slack}|copyPeak={minimumCopy}");
            if (!mutant) { excess.ShouldBeLessThanOrEqualTo(0); }
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
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
}
