using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Live output peaks during drain and append obey the grant plus fixed writer bound and reject retained prefixes (#1789)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_OutputDuringOperation_ShouldBoundLiveHeap(bool grouped, bool mutant)
    {
        // Arrange: each population uses three executions in one warmed engine, each
        // with its own pre-statement baseline. The minimum excludes ambient heap drift.
        long grant = grouped ? 8388608 : 1048576;
        const long writer = 26392, slack = 524288;
        var peaks = new List<long>();
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
                if (retaining) { retained.Add(text); }
                return text;
            }, volatility: SqlFunctionVolatility.Immutable));
            await using var engine = await builder.BuildAsync();
            await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session, count);
            string sql = grouped ? "SELECT id,MIN(k),fresh(id) FROM t GROUP BY id" : "SELECT id,fresh(id) FROM t";
            await using (var warm = (DatabaseResultSet)await session.ExecuteAsync(sql)) { }
            long minimum = long.MaxValue;
            for (int execution = 0; execution < 3; execution++)
            {
                retained.Clear();
                long baseline = LiveHeap.Read(), peak = 0;
                int appended = 0, drained = 0, sampled = 0;
                retaining = mutant;
                SqlRowCollector.ObserveSpill = phase =>
                {
                    if (phase == "output-drained") { drained++; Sample(); }
                    if (phase == "output-append" && ++appended % 512 == 0) { Sample(); }
                };
                try
                {
                    // Act: the mutant keeps the prefix already written to the run
                    // alive until Take, including rows appended after the handoff.
                    await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql);
                    // Assert
                    drained.ShouldBe(1); appended.ShouldBe(count); sampled.ShouldBeGreaterThan(1);
                    minimum = Math.Min(minimum, peak);
                    session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
                    SqlSortSpillMetrics.For(session.LastStatementMetrics).ShouldNotBeNull().Runs.ShouldBe(1);
                    GC.KeepAlive(retained);
                }
                finally { SqlRowCollector.ObserveSpill = null; retaining = false; }
                void Sample() { sampled++; peak = Math.Max(peak, LiveHeap.Read() - baseline); }
            }
            peaks.Add(minimum);
            _output.WriteLine($"OUTPUT_DURING|grouped={grouped}|mutant={mutant}|rows={count}|peak={minimum}|grant={grant}|writer={writer}|slack={slack}");
            void AssertBound() => minimum.ShouldBeLessThanOrEqualTo(grant + writer + slack);
            if (mutant) { Should.Throw<ShouldAssertException>(AssertBound); }
            else { AssertBound(); }
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        if (!mutant) { (peaks[1] - peaks[0]).ShouldBeLessThanOrEqualTo(slack); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Live copy-credit batches obey the fixed 64-record allowance and reject uncharged retained records (#1789, #1838)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CopyCreditDuringOperation_ShouldBoundLiveHeap(bool mutant)
    {
        // Arrange: The NULL source fills G exactly, so no list or record reservation can succeed.
        const long batch = 520472, slack = 32768;
        var excesses = new List<long>();
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
            var retained = new List<byte[]>();
            long minimum = long.MaxValue;
            for (int execution = 0; execution < 3; execution++)
            {
                retained.Clear();
                long baseline = LiveHeap.Read(), peak = 0, admitted = 0, smallest = long.MaxValue;
                int encoded = 0, sampled = 0;
                SqlStatementMetrics? metrics = null;
                SqlStatementMetrics.AfterMemoryReservation = (current, _) => metrics = current;
                SqlPlanExecutor.TableAsSourceAdmitted = bytes => admitted = bytes;
                SqlPlanExecutor.TableAsRecordEncoded = record =>
                {
                    // Models a copy implementation that lets uncharged batches grow
                    // past 64 records instead of releasing them at each flush.
                    if (mutant) { retained.Add(record); }
                    if (++encoded % 512 == 0) { sampled++; peak = Math.Max(peak, LiveHeap.Read() - baseline); }
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
                    minimum = Math.Min(minimum, peak);
                    GC.KeepAlive(retained);
                }
                finally
                {
                    SqlStatementMetrics.AfterMemoryReservation = null; SqlPlanExecutor.TableAsSourceAdmitted = null;
                    SqlPlanExecutor.TableAsRecordEncoded = null; SqlPlanExecutor.TableAsStage = null;
                }
            }
            long excess = minimum - grant - batch - slack; excesses.Add(excess);
            _output.WriteLine($"COPY_CREDIT_DURING|mutant={mutant}|rows={count}|peak={minimum}|grant={grant}|batch={batch}|slack={slack}");
            if (!mutant) { excess.ShouldBeLessThanOrEqualTo(0); }
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        if (mutant) { Should.Throw<ShouldAssertException>(() => excesses.Max().ShouldBeLessThanOrEqualTo(0)); }
    }
}
