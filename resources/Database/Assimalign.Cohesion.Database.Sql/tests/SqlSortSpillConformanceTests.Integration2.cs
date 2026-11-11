using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Foreign-yield shapes expand only batch-4 memory refusals at the same grant and quota (#1789)")]
    [InlineData(false, 262144, 1048576)]
    [InlineData(true, 1048576, 134217728)]
    public async Task ExecuteAsync_ForeignYieldCapability_ShouldMatchFittingRows(bool nested, long budget, long quota)
    {
        // Arrange: the matching batch-4 probes run these exact statements at G and Q.
        await using var engine = await CreateFix2("yield-capability", budget, quota: quota);
        await using var reference = await CreateFix2("yield-capability-fit", 67108864, quota: quota);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var fitting = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await SeedYieldJoin(session, nested); await SeedYieldJoin(fitting, nested);
        string sql = nested
            ? "SELECT a.id,b.id,c.id FROM ja a FULL JOIN (jb b FULL JOIN jc c ON b.id=c.id) ON a.id=b.id LIMIT 1"
            : "SELECT jl.id,jr.id FROM jl FULL JOIN jr ON jl.id=jr.id";
        await using var expected = (DatabaseResultSet)await fitting.ExecuteAsync(sql);
        int handoffs = 0;
        SqlPlanExecutor.ObserveHashSpill = (phase, _) => { if (phase == "join-handoff") { handoffs++; } };
        try
        {
            // Act / Assert
            await using var actual = (DatabaseResultSet)await session.ExecuteAsync(sql);
            var rows = await Rows(actual); rows.ShouldBe(await Rows(expected));
            actual.Columns.Select(column => (column.Ordinal, column.Name, column.Type, column.MaxLength, column.IsNullable))
                .ShouldBe(expected.Columns.Select(column => (column.Ordinal, column.Name, column.Type, column.MaxLength, column.IsNullable)));
            handoffs.ShouldBeGreaterThan(0);
            session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(budget);
            _output.WriteLine($"YIELD_CAPABILITY|nested={nested}|grant={budget}|quota={quota}|rows={rows.Length}|handoffs={handoffs}|digest={Fix2Fingerprint(rows)}");
        }
        finally { SqlPlanExecutor.ObserveHashSpill = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - A foreign reservation's join yield failure releases every owner (#1789)")]
    [InlineData(false, "CK05002")]
    [InlineData(false, "CK05005")]
    [InlineData(false, "cancel")]
    [InlineData(true, "CK05002")]
    [InlineData(true, "CK05005")]
    [InlineData(true, "cancel")]
    public async Task ExecuteAsync_ForeignJoinYieldFailure_ShouldLocateAndRelease(bool nested, string code)
    {
        // Arrange: case (b) is a child's unmatched minimum, case (c) a staged probe reader.
        long budget = nested ? 1048576 : 262144;
        await using var engine = await CreateFix2("foreign-yield", budget, quota: nested ? 134217728 : 1048576);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await SeedYieldJoin(session, nested);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        bool armed = false, injected = false, starved = false, staged = false;
        int handoffs = 0, partitioned = 0;
        long held = 0;
        SqlStatementMetrics? active = null;
        SqlStatementMetrics.AfterMemoryReservation = (metrics, bytes) =>
        {
            active = metrics;
            if (!nested || starved || bytes != SqlHashJoinSpill.DirectoryBytes(16)) { return; }
            starved = true; held = budget - metrics.HashMemoryBytes - 45000;
            metrics.ReserveHashMemory(held, budget);
        };
        SqlRowCollector.ObserveSpill = phase => staged |= phase == "staged-drained";
        SqlPlanExecutor.ObserveHashSpill = (phase, _) =>
        {
            if (phase == "join-handoff") { handoffs++; }
            if (nested && phase == "join-partitioned" && ++partitioned == 1)
            {
                active!.ReleaseHashMemory(held); held = budget - active.HashMemoryBytes - 300000;
                active.ReserveHashMemory(held, budget);
            }
            if (phase == "join-sorter-yield") { armed = true; }
        };
        BeforeWrite(engine.TempSpace!, () =>
        {
            if (!armed) { return; }
            injected = true;
            if (code == "cancel") { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            throw TempException(code, "injected yield write failure");
        });
        string sql = nested
            ? "SELECT a.id,b.id,c.id FROM ja a FULL JOIN (jb b FULL JOIN jc c ON b.id=c.id) ON a.id=b.id LIMIT 1"
            : "SELECT jl.id,jr.id FROM jl FULL JOIN jr ON jl.id=jr.id";
        try
        {
            // Act / Assert
            if (code == "cancel")
            { await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(sql, cancellationToken: cancellation.Token)); }
            else
            {
                var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: cancellation.Token));
                failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
                failure.Diagnostics[0].Start.ShouldNotBeNull(); failure.Diagnostics[0].End.ShouldNotBeNull();
            }
            armed.ShouldBeTrue(); injected.ShouldBeTrue(); handoffs.ShouldBe(nested ? 2 : 1);
            if (nested) { starved.ShouldBeTrue(); partitioned.ShouldBe(1); }
            else { staged.ShouldBeTrue(); }
        }
        finally
        {
            active?.ReleaseHashMemory(held);
            BeforeWrite(engine.TempSpace!, null); SqlStatementMetrics.AfterMemoryReservation = null;
            SqlPlanExecutor.ObserveHashSpill = null; SqlRowCollector.ObserveSpill = null;
        }
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        Statements(engine.TempSpace!).ShouldBeEmpty(); TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        (await session.ExecuteAsync("SELECT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - An impossible reservation refuses without consolidating an optional join arena (#1789)")]
    public async Task ReserveHashMemory_HopelessYield_ShouldRefuseWithoutTempWrite()
    {
        // Arrange: register a real optional arena after the fitting build's refusal.
        const long budget = 262144;
        await using var engine = await CreateFix2("hopeless-yield", budget, quota: 67108864);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        var metrics = new SqlStatementMetrics();
        using var table = new SqlHashJoinTable(new SqlHashJoinPath([0], [0], [Collation.Binary]), metrics, budget, false);
        bool spilled = false;
        int writes = 0, yields = 0;
        SqlPlanExecutor.ObserveHashSpill = (phase, _) => { if (phase == "join-sorter-yield") { yields++; } };
        try
        {
            for (int i = 0; i < 400; i++)
            {
                object?[] row = [i, new string('x', 512)];
                try { table.Add(row, CancellationToken.None); }
                catch (SqlExecutionException failure) when (!spilled && failure.Diagnostics[0].Code == "CS05067")
                { table.Spill(session, row, CancellationToken.None, failure); spilled = true; }
            }
            SqlHashSpillMetrics.For(metrics).ShouldNotBeNull().HasOptionalArenas.ShouldBeTrue();
            BeforeWrite(engine.TempSpace!, () => writes++);
            // Act / Assert: even all of G cannot cover G+1, regardless of arena sizing.
            Should.Throw<SqlExecutionException>(() => metrics.ReserveHashMemory(budget + 1, budget)).Diagnostics[0].Code.ShouldBe("CS05067");
            spilled.ShouldBeTrue();
            writes.ShouldBe(0);
            yields.ShouldBe(0);
        }
        finally
        {
            BeforeWrite(engine.TempSpace!, null); SqlPlanExecutor.ObserveHashSpill = null;
            table.Dispose(); SqlHashSpillMetrics.DetachJoinTemp(metrics)?.Dispose();
        }
        metrics.HashMemoryBytes.ShouldBe(0);
    }

    private static async Task SeedYieldJoin(SqlDatabaseSession session, bool nested = false)
    {
        if (!nested)
        {
            await session.ExecuteAsync("CREATE TABLE jl(id INT,payload TEXT)");
            await session.ExecuteAsync("INSERT INTO jl VALUES" + string.Join(',', Enumerable.Range(0, 200).Select(i => $"({i},'{new string('x', 2500)}')")));
            await session.ExecuteAsync("CREATE TABLE jr(id INT," + string.Join(',', Enumerable.Range(0, 150).Select(i => $"b{i} CHAR(30)")) + ")");
            string cells = string.Join(',', Enumerable.Repeat("'" + new string('y', 30) + "'", 150));
            await session.ExecuteAsync("INSERT INTO jr VALUES" + string.Join(',', Enumerable.Range(0, 50).Select(i => $"({i},{cells})")));
            return;
        }
        await session.ExecuteAsync("CREATE TABLE ja(id INT,payload TEXT)");
        await session.ExecuteAsync("INSERT INTO ja VALUES" + string.Join(',', Enumerable.Range(0, 250).Select(i => $"({i},'{new string('x', 2500)}')")));
        await session.ExecuteAsync("CREATE TABLE jb(id INT)");
        for (int start = 0; start < 20000; start += 200)
        { await session.ExecuteAsync("INSERT INTO jb VALUES" + string.Join(',', Enumerable.Range(start, 200).Select(i => $"({i})"))); }
        await session.ExecuteAsync("CREATE TABLE jc(id INT," + string.Join(',', Enumerable.Range(0, 150).Select(i => $"c{i} CHAR(45)")) + ")");
        var random = new Random(178911);
        var values = new List<string>();
        for (int i = 0; i < 50; i++)
        {
            var cells = new List<string>();
            for (int column = 0; column < 150; column++)
            { byte[] bytes = new byte[33]; random.NextBytes(bytes); cells.Add("'" + Convert.ToBase64String(bytes) + "x'"); }
            values.Add($"({i},{string.Join(',', cells)})");
        }
        await session.ExecuteAsync("INSERT INTO jc VALUES" + string.Join(',', values));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Computed common-type join keys expose the spill eligibility boundary (#1788, #1789)")]
    [InlineData("INNER")]
    [InlineData("LEFT")]
    [InlineData("RIGHT")]
    [InlineData("FULL")]
    public async Task ExecuteAsync_CommonComputedJoinKey_ShouldRetainExplicitSpillBoundary(string kind)
    {
        // Arrange: G=262,144 B, Q=67,108,864 B. Computed keys are explicitly excluded
        // from SQ-L3b; extending their retained/replayed representation needs an owner decision.
        await using var engine = await Create("common-computed-join", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE jl(id INT,payload TEXT)");
        await session.ExecuteAsync("CREATE TABLE jr(id INT,n NUMERIC,payload TEXT)");
        for (int first = 0; first < 2500; first += 100)
        {
            await session.ExecuteAsync("INSERT INTO jl VALUES" + string.Join(',', Enumerable.Range(first, 100).Select(i => $"({i},'{new string('x', 512)}')")));
            await session.ExecuteAsync("INSERT INTO jr VALUES" + string.Join(',', Enumerable.Range(first, 100).Select(i => $"({i},{i}.0,'{new string('x', 512)}')")));
        }
        string sql = $"SELECT l.id,r.id FROM jl l {kind} JOIN jr r ON COALESCE(l.id,0)=r.n LIMIT 1";

        // Act / Assert: no indexed handoff can occur on this currently excluded path.
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql));
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05067");
        SqlHashSpillMetrics.For(session.LastStatementMetrics!).ShouldBeNull();
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        TempBytes(engine.TempSpace!).ShouldBe(0);
    }
}
