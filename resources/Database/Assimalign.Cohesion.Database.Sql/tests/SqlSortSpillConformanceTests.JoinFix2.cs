using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
#if !L3B_BASE
    [Fact(DisplayName = "Cohesion Test [Sql] - A closed delivered borrower leaves ownership with the statement root (#1789)")]
    public async Task CloseJoinTemp_JoinFix2ClosedResult_ShouldKeepRootOwnership()
    {
        // Arrange: session disposal can run after attachment and before root delivery.
        await using var engine = await Create("closed-borrower", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var columns = (DatabaseResultSet)await session.ExecuteAsync("SELECT 1 AS id");
        var metrics = new SqlStatementMetrics();
        var statement = SqlHashSpillMetrics.JoinTemp(metrics, session);
        try
        {
            var run = statement.CreateRun();
            object?[] row = [1L];
            SqlTempRowCodec.TryEncode(row, out var bytes).ShouldBeTrue();
            run.Append(bytes, CancellationToken.None); run.Seal(CancellationToken.None);
            // The reviewed product owned this statement from construction; this pass
            // lends it until root delivery. Reflection keeps the ordering probe portable.
            var constructor = typeof(SqlSpilledResultSet).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters()[2].ParameterType == typeof(StorageTempRun));
            var arguments = new List<object?> { columns.Columns, statement, run, 0L, -1L, session, 262144L, SqlTempRowCodec.DecodedBytes(row) };
            if (constructor.GetParameters().Length == 9) { arguments.Add(false); }
            var result = (SqlSpilledResultSet)constructor.Invoke(arguments.ToArray());
            // Act
            await session.DisposeAsync();
            if (Statements(engine.TempSpace!).Count != 0)
            {
                typeof(SqlPlanExecutor).GetMethod("CloseJoinTemp", BindingFlags.Static | BindingFlags.NonPublic)
                    .ShouldNotBeNull().Invoke(null, [metrics, result, null]);
            }
            // Assert
            Statements(engine.TempSpace!).ShouldBeEmpty();
        }
        finally { SqlHashSpillMetrics.DetachJoinTemp(metrics); statement.Dispose(); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ANALYZE reports actual hash spill permission (#1789)")]
    [InlineData("INNER", false, 262144)]
    [InlineData("LEFT", false, 262144)]
    [InlineData("RIGHT", false, 262144)]
    [InlineData("FULL", false, 262144)]
    [InlineData("INNER", true, 65536)]
    [InlineData("LEFT", true, 65536)]
    [InlineData("RIGHT", true, 65536)]
    [InlineData("FULL", true, 65536)]
    public async Task ExecuteAsync_JoinFix2ExplainPermission_ShouldReportNoSpill(string kind, bool temp, long budget)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("explain-permission");
        builder.Options.StatementMemoryBudget = budget;
        if (temp) { builder.Options.TempDirectory = Path.Combine(_root, "permission"); }
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await SeedFix2(session, 8, 512);
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync($"EXPLAIN ANALYZE SELECT l.id,r.id FROM jl l {kind} JOIN jr r ON l.id=r.id");
        string text = string.Join('\n', (await Rows(result)).Select(r => r.ToString()));
        // Assert
        text.ShouldContain("hash_budget=no_spill", Case.Sensitive);
        text.ShouldContain("hash_status=fitting", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Later buffers never recreate a completed FULL debt (#1789)")]
    public async Task ExecuteAsync_JoinFix2CompletedDebt_ShouldUseOrdinaryCollector()
    {
        // Arrange
        await using var engine = await Create("completed-debt", 1048576);
        await using var reference = await CreateFix2("completed-reference", long.MaxValue);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        foreach (var session in new[] { a, b })
        {
            await SeedFix2Owner(session, 1000);
            foreach (string table in new[] { "jc", "jd" })
            { await session.ExecuteAsync($"CREATE TABLE {table}(id INT,payload TEXT)"); await session.ExecuteAsync($"INSERT INTO {table} SELECT id,payload FROM jl"); }
        }
        const string sql = "SELECT l.id,l.payload FROM (jl l FULL JOIN jr r ON l.id=r.id) JOIN (jc c JOIN jd d ON c.id=d.id) ON l.id=c.id ORDER BY l.id LIMIT 600";
        await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
        bool ordinary = false, deferred = false, full = false; int sequence = 0, handoff = -1, staged = -1;
        SqlStatementMetrics? active = null;
        SqlStatementMetrics.AfterMemoryReservation = (metrics, _) => active = metrics;
        SqlPlanExecutor.ObserveHashSpill = (phase, _) =>
        {
            if (phase == "join-handoff" && !full) { handoff = ++sequence; }
            if (phase == "join-input" && handoff >= 0 && SqlHashSpillMetrics.For(active!)?.FullJoin == true) { full = true; }
        };
        SqlRowCollector.ObserveSpill = phase =>
        { ordinary |= phase == "handoff"; deferred |= phase == "deferred"; if (phase == "staged-drained" && full) { staged = ++sequence; } };
        try
        {
            // Act
            var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
            try { (await Rows(actual)).ShouldBe(await Rows(expected)); }
            finally { await actual.DisposeAsync(); }
            // Assert
            ordinary.ShouldBeTrue(); deferred.ShouldBeFalse(); full.ShouldBeTrue(); staged.ShouldBeGreaterThan(handoff);
            Statements(engine.TempSpace!).ShouldBeEmpty();
        }
        finally { SqlRowCollector.ObserveSpill = null; SqlPlanExecutor.ObserveHashSpill = null; SqlStatementMetrics.AfterMemoryReservation = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Indexed builds complete beyond the open-run limit (#1789)")]
    [InlineData(262144, 5000)]
    [InlineData(1048576, 2800)]
    [InlineData(16777216, 44800)]
    public async Task ExecuteAsync_JoinFix2MergePasses_ShouldComplete(long budget, int count)
    {
        // Arrange: an explicit quota isolates merge policy from Seal's coexistence quota.
        await using var engine = await Create("merge-passes", budget);
        await using var reference = await CreateFix2("merge-reference", long.MaxValue);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await SeedFix2(a, count, 512, randomPayload: true); await SeedFix2(b, count, 512, randomPayload: true);
        const string sql = "SELECT l.id,r.id FROM jl l LEFT JOIN jr r ON l.id=r.id LIMIT 64";
        await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
        // Act
        var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
        try { (await Rows(actual)).ShouldBe(await Rows(expected)); }
        finally { await actual.DisposeAsync(); }
        // Assert
        b.LastStatementMetrics!.JoinCandidatePairs.ShouldBe(a.LastStatementMetrics!.JoinCandidatePairs);
        b.LastStatementMetrics.RecordsExamined.ShouldBe(a.LastStatementMetrics.RecordsExamined);
        var spill = SqlHashSpillMetrics.For(b.LastStatementMetrics).ShouldNotBeNull();
        _output.WriteLine($"FIX2_MERGES|grant={budget}|rows={count}|encoded_build={count * 560L}|runs={spill.Runs}|passes={spill.MergePasses}");
        if (budget == 262144) { spill.Runs.ShouldBeGreaterThan(32); }
        Statements(engine.TempSpace!).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - FULL unmatched sorting admits its own minimum grant (#1789)")]
    public async Task Unmatched_JoinFix2Minimum_ShouldUseAvailableGrant()
    {
        // Arrange
        await using var engine = await Create("unmatched-minimum", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        var metrics = new SqlStatementMetrics(); var keys = new SqlHashJoinPath([0], [0], [Collation.Binary]);
        using var table = new SqlHashJoinTable(keys, metrics, 262144, trackUnmatched: true);
        bool spilled = false;
        try
        {
            for (int i = 0; i < 400; i++)
            {
                object?[] row = [1L, new string('x', 512)];
                try { table.Add(row, CancellationToken.None); }
                catch (SqlExecutionException error) when (!spilled && error.Diagnostics[0].Code == "CS05067")
                { table.Spill(session, row, CancellationToken.None, error); spilled = true; }
            }
            table.Probe([2L], CancellationToken.None).Count().ShouldBe(0);
            metrics.ReserveHashMemory(150000, 262144);
            // Act / Assert
            table.Unmatched(CancellationToken.None).Count().ShouldBe(400);
            metrics.ReleaseHashMemory(150000);
        }
        finally { table.Dispose(); SqlHashSpillMetrics.DetachJoinTemp(metrics)?.Dispose(); }
        Statements(engine.TempSpace!).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Optional sorter memory gives way to later wider rows (#1789)")]
    public async Task Add_JoinFix2WiderRow_ShouldYieldOptionalArena()
    {
        // Arrange
        const long budget = 1048576;
        await using var engine = await Create("wider-row", budget);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        var metrics = new SqlStatementMetrics(); var keys = new SqlHashJoinPath([0], [0], [Collation.Binary]);
        using var table = new SqlHashJoinTable(keys, metrics, budget);
        bool spilled = false;
        long held = 0;
        try
        {
            for (int i = 0; i < 1100; i++)
            {
                object?[] row = [1L, new string('x', 512)];
                try { table.Add(row, CancellationToken.None); }
                catch (SqlExecutionException error) when (!spilled && error.Diagnostics[0].Code == "CS05067")
                { table.Spill(session, row, CancellationToken.None, error); spilled = true; }
            }
            held = budget - metrics.HashMemoryBytes - 20000; metrics.ReserveHashMemory(held, budget);
            // Act
            table.Add([1L, new string('y', 15000)], CancellationToken.None);
            metrics.ReleaseHashMemory(held); held = 0;
            // Assert
            table.Probe([1L], CancellationToken.None).Count().ShouldBe(1101);
        }
        finally { metrics.ReleaseHashMemory(held); table.Dispose(); SqlHashSpillMetrics.DetachJoinTemp(metrics)?.Dispose(); }
        metrics.HashMemoryBytes.ShouldBe(0); Statements(engine.TempSpace!).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Real spilled joins pin the reservations made by the product (#1789)")]
    [InlineData("FULL")]
    [InlineData("staged")]
    [InlineData("collector")]
    public async Task ExecuteAsync_JoinFix2Reservations_ShouldBindProductCharges(string shape)
    {
        // Arrange
        long budget = shape == "FULL" ? 262144 : 1048576;
        await using var engine = await CreateFix2("reservations", budget, quota: shape == "collector" ? 134217728 : null);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        if (shape == "staged") { await SeedFix2(session, 3000, 128); }
        else
        {
            int count = shape == "FULL" ? 24 : 72;
            int width = shape == "FULL" ? 5800 : 7800;
            await SeedFix2(session, count, width);
            await session.ExecuteAsync($"UPDATE jr SET payload='{new string('x', width + 100)}' WHERE id={count - 1}");
            await session.ExecuteAsync("UPDATE jl SET id=1"); await session.ExecuteAsync("UPDATE jr SET id=1");
        }
        var reservations = new SortedDictionary<long, int>();
        bool live = false;
        long unmatchedGrant = -1;
        SqlPlanExecutor.ObserveHashSpill = (phase, value) =>
        { if (phase == "join-handoff") { live = true; } if (phase == "join-match-sort-live") { unmatchedGrant = value; } };
        SqlStatementMetrics.AfterMemoryReservation = (_, bytes) =>
        { if (live) { reservations[bytes] = reservations.GetValueOrDefault(bytes) + 1; } };
        string sql = shape switch
        {
            "FULL" => "SELECT l.id,r.id FROM jl l FULL JOIN jr r ON l.id=r.id LIMIT 1",
            "staged" => "SELECT stamp(l.id),r.id FROM jl l LEFT JOIN jr r ON l.id=r.id ORDER BY r.id LIMIT 1",
            _ => "SELECT l.id,r.payload FROM jl l FULL JOIN jr r ON l.id=r.id ORDER BY r.id LIMIT 600",
        };
        try
        {
            // Act
            var result = (DatabaseResultSet)await session.ExecuteAsync(sql);
            await Rows(result); await result.DisposeAsync();
            // Assert: the values come from successful product reservations, not formulas.
            string trace = string.Join(',', reservations.Select(p => $"{p.Key}:{p.Value}"));
            _output.WriteLine($"FIX2_RESERVATIONS|{shape}|peak={session.LastStatementMetrics!.PeakHashMemoryBytes}|unmatched_grant={unmatchedGrant}|{trace}");
            trace.ShouldBe(shape switch
            {
                "FULL" => "32:1,136:1,400:1,792:1,37737:1,38832:1,65536:1,196717:1,259823:1",
                "staged" => "792:1,840:901,1280:1,8704:1,10121:1,25208:1,25920:1,262144:1,592653:1,1037175:1",
                _ => "40:1,400:1,792:1,1352:2,15856:41,16056:1,16382:1,25112:1,47737:1,57072:1,262144:1,618413:1,1019919:1,1030842:1",
            });
            if (shape == "FULL") { unmatchedGrant.ShouldBe(220991); }
            Statements(engine.TempSpace!).ShouldBeEmpty();
        }
        finally { SqlPlanExecutor.ObserveHashSpill = null; SqlStatementMetrics.AfterMemoryReservation = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - An uncacheable decoded build row refuses before sorting (#1789)")]
    public async Task Add_JoinFix2DecodedRow_ShouldRefuseAtAdmission()
    {
        // Arrange
        const long budget = 786432;
        await using var engine = await Create("decoded-admission", budget);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        var metrics = new SqlStatementMetrics(); var keys = new SqlHashJoinPath([0], [0], [Collation.Binary]);
        using var table = new SqlHashJoinTable(keys, metrics, budget);
        using var spill = new SqlHashJoinSpill(session, keys, metrics, budget, false);
        try
        {
            spill.AdoptBuild(table, CancellationToken.None);
            object?[] row = Enumerable.Repeat<object?>(true, 7000).ToArray();
            // Act / Assert
            SqlExecutionException? admission = null;
            try { spill.Add(row, CancellationToken.None); }
            catch (SqlExecutionException error) { admission = error; }
            if (admission is null)
            {
                var seal = Should.Throw<SqlExecutionException>(() => spill.Probe([true], CancellationToken.None).Count());
                _output.WriteLine($"FIX2_DECODED|admission=accepted|seal={seal.Diagnostics[0].Code}");
                seal.Diagnostics[0].Code.ShouldBe("CS05067");
            }
            admission.ShouldNotBeNull().Diagnostics[0].Code.ShouldBe("CS05067");
        }
        finally { spill.Dispose(); SqlHashSpillMetrics.DetachJoinTemp(metrics)?.Dispose(); }
        Statements(engine.TempSpace!).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Removing a session preserves provisioning identity (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithoutSession_JoinFix2Provisioning_ShouldKeepNames(bool checkpoint)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("context", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var context = new SqlStatementContext(transaction, database.Coordinator, "schema", "db",
                metadataLockTimeout: TimeSpan.FromSeconds(17), checkpointCopy: checkpoint);
            var attached = context.WithSession(session);
            // Act
            var stripped = attached.WithoutSession();
            // Assert
            stripped.Session.ShouldBeNull(); attached.Session.ShouldBeSameAs(session);
            stripped.ProvisioningSchema.ShouldBe("schema"); stripped.DatabaseName.ShouldBe("db");
            stripped.MetadataLockTimeout.ShouldBe(TimeSpan.FromSeconds(17)); stripped.CheckpointCopy.ShouldBe(checkpoint);
            if (checkpoint) { stripped.CopyCheckpointBytes.ShouldBe(context.CopyCheckpointBytes); }
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - EXPLAIN describes fitting and spilled hash policy (#1789)")]
    [InlineData("INNER", 24)]
    [InlineData("LEFT", 24)]
    [InlineData("RIGHT", 24)]
    [InlineData("FULL", 24)]
    [InlineData("INNER", 400)]
    [InlineData("LEFT", 400)]
    [InlineData("RIGHT", 400)]
    [InlineData("FULL", 400)]
    public async Task ExecuteAsync_JoinFix2Explain_ShouldDescribePolicy(string kind, int count)
    {
        // Arrange
        await using var engine = await CreateFix2("explain", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await SeedFix2(session, count, 512);
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync($"EXPLAIN ANALYZE SELECT l.id,r.id FROM jl l {kind} JOIN jr r ON l.id=r.id");
        string text = string.Join('\n', (await Rows(result)).Select(r => r.ToString()));
        _output.WriteLine(text);
        // Assert
        text.ShouldContain("hash_budget=spill_on_refusal", Case.Sensitive);
        text.ShouldContain(count == 24 ? "hash_status=fitting" : "hash_status=spilled", Case.Sensitive);
        text.Contains("bounded in-memory hash", StringComparison.Ordinal).ShouldBe(count == 24);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Build handoff uses the minimum when a greedy grant cannot fit (#1789)")]
    public async Task ExecuteAsync_JoinFix2Minimum_ShouldAdmitWiderBuild()
    {
        // Arrange: A is inside the review's [136665,142258) band.
        await using var engine = await Create("minimum", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        var metrics = new SqlStatementMetrics(); metrics.ReserveHashMemory(122144, 262144);
        var keys = new SqlHashJoinPath([0], [0], [Collation.Binary]);
        using var table = new SqlHashJoinTable(keys, metrics, 262144);
        object?[] current = [1L, new string('x', 5900)];
        bool spilled = false;
        try
        {
            // Act
            for (int i = 0; i < 20; i++)
            {
                try { table.Add(current, CancellationToken.None); }
                catch (SqlExecutionException error) when (!spilled && error.Diagnostics[0].Code == "CS05067")
                { table.Spill(session, current, CancellationToken.None, error); spilled = true; }
            }
            metrics.ReleaseHashMemory(122144);
            // Assert
            spilled.ShouldBeTrue(); table.Probe([1L], CancellationToken.None).Count().ShouldBe(20);
        }
        finally { table.Dispose(); SqlHashSpillMetrics.DetachJoinTemp(metrics)?.Dispose(); }
        metrics.HashMemoryBytes.ShouldBe(0); Statements(engine.TempSpace!).ShouldBeEmpty();
    }

#endif
    [Theory(DisplayName = "Cohesion Test [Sql] - Nested FULL unmatched debts retain deferral at small grants (#1789)")]
    [InlineData(262144, false)]
    [InlineData(393216, false)]
    [InlineData(393216, true)]
    public async Task ExecuteAsync_JoinFix2FullDebts_ShouldComplete(long budget, bool smallLeft)
    {
        // Arrange
        await using var reference = await CreateFix2("debts-reference", long.MaxValue);
        await using var engine = await Create("debts", budget);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        foreach (var session in new[] { a, b })
        {
            await SeedFix2Owner(session, 2500);
            await session.ExecuteAsync("CREATE TABLE jt(id INT,payload TEXT)");
            if (smallLeft)
            {
                await session.ExecuteAsync("DELETE FROM jl");
                string values = string.Join(',', Enumerable.Range(1000, 3).Select(i => $"({i},'{new string('x', 512)}')"));
                await session.ExecuteAsync("INSERT INTO jl VALUES" + values);
                await session.ExecuteAsync("INSERT INTO jt VALUES" + values);
                await session.ExecuteAsync("UPDATE jr SET id=1000+(id%3)");
            }
            else { await session.ExecuteAsync("INSERT INTO jt SELECT id,payload FROM jr"); }
        }
        const string sql = "SELECT l.id,r.id,t.payload FROM jl l FULL JOIN (jr r FULL JOIN jt t ON r.id=t.id) ON l.id=r.id ORDER BY t.id IS NULL LIMIT 600";
        await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
        var expectedRows = await Rows(expected);
        _output.WriteLine($"FIX2_FULL_REFERENCE|grant={budget}|small={smallLeft}|digest={Fix2Fingerprint(expectedRows)}|pairs={a.LastStatementMetrics!.JoinCandidatePairs}|records={a.LastStatementMetrics.RecordsExamined}");
        // Act
        var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
        try { (await Rows(actual)).ShouldBe(expectedRows); }
        finally { await actual.DisposeAsync(); }
        // Assert
        Statements(engine.TempSpace!).ShouldBeEmpty();
#if !L3B_BASE
        SqlHashSpillMetrics.For(b.LastStatementMetrics!).ShouldNotBeNull().FullJoin.ShouldBeFalse();
#endif
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Shared staging preserves fitting calls and the first error (#1789)")]
    [InlineData("INNER", false)]
    [InlineData("LEFT", false)]
    [InlineData("RIGHT", false)]
    [InlineData("FULL", false)]
    [InlineData("INNER", true)]
    [InlineData("LEFT", true)]
    [InlineData("RIGHT", true)]
    [InlineData("FULL", true)]
    public async Task ExecuteAsync_JoinFix2Staging_ShouldAddCapability(string kind, bool throws)
    {
        foreach (var (count, budget, refuses) in new[] { (400, 262144L, true), (16, 262144L, false), (400, 2097152L, false) })
        {
            // Arrange: the 400-row/256 KiB setup also refuses on b5602544d.
            var calls = new List<long>();
            await using var engine = await CreateFix2($"staging-{count}-{budget}", budget, calls);
            await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
            await SeedFix2(session, count, 512);
            string sql = $"SELECT {(throws ? "fails_on" : "stamp")}(l.id),r.id FROM jl l {kind} JOIN jr r ON l.id=r.id ORDER BY r.id LIMIT 37";
            // Act / Assert
            if (refuses || throws && count > 317)
            {
                var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql));
                _output.WriteLine($"FIX2_STAGING|{kind}|grant={budget}|rows={count}|error={failure.Diagnostics[0].Code}|calls={calls.Count}|candidates={session.LastStatementMetrics!.JoinCandidatePairs}");
                failure.Diagnostics[0].Code.ShouldBe(refuses ? "CS05067" : "CS05007");
                calls.ShouldBe(Enumerable.Range(0, refuses ? 0 : 318).Select(i => (long)i));
            }
            else
            {
                var result = (DatabaseResultSet)await session.ExecuteAsync(sql);
                try
                {
                    var rows = await Rows(result); rows.Length.ShouldBe(Math.Min(37, count));
                    rows.Select(r => r.ToString()).ShouldBe(Enumerable.Range(0, Math.Min(37, count)).Select(i => $"{i}|{i}"));
                    _output.WriteLine($"FIX2_FITTING|{kind}|grant={budget}|rows={count}|digest={Fix2Fingerprint(rows)}|calls={calls.Count}");
                    calls.ShouldBe(Enumerable.Range(0, count).Select(i => (long)i));
                }
                finally { await result.DisposeAsync(); }
            }
            if (!refuses)
            {
                session.LastStatementMetrics!.JoinCandidatePairs.ShouldBe(count);
                session.LastStatementMetrics.RecordsExamined.ShouldBe(2 * count);
            }
            Statements(engine.TempSpace!).ShouldBeEmpty();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Normalized probe staging admits wide keys and payloads (#1789)")]
    [InlineData("FULL", true)]
    [InlineData("FULL", false)]
    [InlineData("RIGHT", true)]
    [InlineData("RIGHT", false)]
    public async Task ExecuteAsync_JoinFix2Probe_ShouldPreserveFittingRows(string kind, bool key)
    {
        foreach (var (count, budget, refuses) in new[] { (400, 262144L, true), (40, 262144L, false), (400, 2097152L, false) })
        {
            // Arrange
            await using var engine = await CreateFix2($"probe-{count}-{budget}", budget);
            await using var reference = await CreateFix2($"probe-reference-{count}-{budget}", long.MaxValue);
            await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
            await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
            foreach (var session in new[] { a, b })
            {
                await SeedFix2(session, count, 512, textKey: true);
                // RIGHT's physical outer is jr. FULL's physical outer is jl.
                string table = kind == "RIGHT" ? "jr" : "jl";
                await session.ExecuteAsync($"UPDATE {table} SET {(key ? "id" : "payload")}='{new string('w', 7000)}' WHERE id='1'");
            }
            string sql = $"SELECT l.id,r.id FROM jl l {kind} JOIN jr r ON l.id=r.id";
            await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
            // Act / Assert
            if (refuses)
            {
                var failure = await Should.ThrowAsync<DatabaseException>(async () => await b.ExecuteAsync(sql));
                failure.Diagnostics[0].Code.ShouldBe("CS05067");
            }
            else
            {
                var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
                try
                {
                    var rows = await Rows(actual); rows.ShouldBe(await Rows(expected));
                    _output.WriteLine($"FIX2_PROBE|{kind}|key={key}|grant={budget}|rows={count}|digest={Fix2Fingerprint(rows)}");
                }
                finally { await actual.DisposeAsync(); }
                b.LastStatementMetrics!.JoinCandidatePairs.ShouldBe(a.LastStatementMetrics!.JoinCandidatePairs);
                b.LastStatementMetrics.RecordsExamined.ShouldBe(a.LastStatementMetrics.RecordsExamined);
            }
            Statements(engine.TempSpace!).ShouldBeEmpty();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Materialized group inputs never dispose delivered shared runs (#1789)")]
    [InlineData("BOOL_AND")]
    [InlineData("exact-control")]
    [InlineData("BOOL_OR")]
    [InlineData("EVERY")]
    [InlineData("key")]
    [InlineData("argument")]
    [InlineData("collector")]
    public async Task ExecuteAsync_JoinFix2Ownership_ShouldReadAndReleaseTwentyTimes(string shape)
    {
        // Arrange
        var expectedCalls = new List<long>(); var actualCalls = new List<long>();
        await using var reference = await CreateFix2("owner-reference", long.MaxValue, expectedCalls);
        await using var engine = await Create("owner", 1048576, actualCalls);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        if (shape == "collector")
        { await SeedFix2(a, 10000, 0); await SeedFix2(b, 10000, 0); }
        else { await SeedFix2Owner(a, 5000); await SeedFix2Owner(b, 5000); }
        string group = shape == "key" ? "stamp(l.id / 4)" : "l.id / 4";
        string aggregate = shape == "argument" ? "BOOL_AND(stamp(l.id) IS NULL)" : $"{(shape is "key" or "exact-control" ? "BOOL_AND" : shape)}(r.payload IS NULL)";
        string sql = shape == "collector"
            ? $"SELECT stamp(l.id) AS k,CAST(l.id AS TEXT)||'{new string('x', 1000)}' pad FROM jl l LEFT JOIN jr r ON l.id=r.id WHERE l.id<600 ORDER BY k"
            : $"SELECT {group} AS g,{aggregate} AS b,{(shape == "exact-control" ? "" : $"CAST({group} AS TEXT)||")}'{new string('x', 1000)}' AS pad FROM jl l LEFT JOIN jr r ON l.id=r.id GROUP BY {group} ORDER BY g";
        await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
        var rows = await Rows(expected);
        long pairs = a.LastStatementMetrics!.JoinCandidatePairs, records = a.LastStatementMetrics.RecordsExamined;
        _output.WriteLine($"FIX2_REFERENCE|{shape}|rows={rows.Length}|digest={Fix2Fingerprint(rows)}|calls={expectedCalls.Count}|pairs={pairs}|records={records}");
        bool staged = false, collected = false, joined = false;
        SqlRowCollector.ObserveSpill = phase =>
        { staged |= phase is "staged-drained" or "staged-sort" or "output-spool"; collected |= phase == "handoff"; };
        SqlPlanExecutor.ObserveHashSpill = (phase, _) => { joined |= phase == "join-handoff"; };
        int repetitions = int.TryParse(Environment.GetEnvironmentVariable("L3B_REPETITIONS"), out int selected) && selected > 0 ? selected : 20;
        try
        {
            for (int i = 0; i < repetitions; i++)
            {
                actualCalls.Clear(); staged = false; collected = false; joined = false;
                // Act
                var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
                if (i == 0) { _output.WriteLine($"FIX2_OWNER|{shape}|{actual.GetType().Name}"); }
                try { (await Rows(actual)).ShouldBe(rows); }
                finally { await actual.DisposeAsync(); }
                // Assert
                actualCalls.ShouldBe(expectedCalls);
                b.LastStatementMetrics!.JoinCandidatePairs.ShouldBe(pairs); b.LastStatementMetrics.RecordsExamined.ShouldBe(records);
                b.LastStatementMetrics.HashMemoryBytes.ShouldBe(0); b.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
                Statements(engine.TempSpace!).ShouldBeEmpty();
                if (shape == "collector") { joined.ShouldBeTrue(); collected.ShouldBeTrue(); staged.ShouldBeFalse(); }
                else if (shape != "exact-control") { (joined && staged && collected).ShouldBeTrue(); }
            }
        }
        finally { SqlRowCollector.ObserveSpill = null; SqlPlanExecutor.ObserveHashSpill = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Incompressible indexed builds respect Seal coexistence (#1789)")]
    [InlineData(400, false)]
    [InlineData(1200, true)]
    public async Task ExecuteAsync_JoinFix2TempCapacity_ShouldLocateAndRelease(int count, bool refuses)
    {
        // Arrange
        await using var engine = await CreateFix2("capacity", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await SeedFix2(session, count, 512, randomPayload: true);
        const string sql = "SELECT l.id,r.id FROM jl l LEFT JOIN jr r ON l.id=r.id";
        // Act / Assert
        if (refuses)
        {
            long inputs = 0; bool partitioned = false; int sealWrites = 0;
            SqlPlanExecutor.ObserveHashSpill = (phase, value) => { if (phase == "join-input") { inputs = value; } partitioned |= phase == "join-partitioned"; };
            BeforeWrite(engine.TempSpace!, () =>
            {
                if (inputs == 1200 && new System.Diagnostics.StackTrace().ToString().Contains("SqlHashJoinSpill.Seal(", StringComparison.Ordinal)) { sealWrites++; }
            });
            DatabaseException error;
            try { error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql)); }
            finally { SqlPlanExecutor.ObserveHashSpill = null; BeforeWrite(engine.TempSpace!, null); }
            inputs.ShouldBe(1200); partitioned.ShouldBeFalse(); sealWrites.ShouldBeGreaterThan(0);
            error.Diagnostics[0].Code.ShouldBe("CK05002"); error.Diagnostics[0].Location.ShouldNotBeNull();
            _output.WriteLine($"FIX2_QUOTA|{error.Diagnostics[0]}|{error.StackTrace}");
        }
        else
        {
            await using var reference = await CreateFix2("capacity-reference", long.MaxValue);
            await using var other = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
            await SeedFix2(other, count, 512, randomPayload: true);
            await using var expected = (DatabaseResultSet)await other.ExecuteAsync(sql);
            var expectedRows = await Rows(expected);
            _output.WriteLine($"FIX2_CAPACITY_REFERENCE|rows={count}|digest={Fix2Fingerprint(expectedRows)}|pairs={other.LastStatementMetrics!.JoinCandidatePairs}|records={other.LastStatementMetrics.RecordsExamined}");
            var result = (DatabaseResultSet)await session.ExecuteAsync(sql);
            try { (await Rows(result)).ShouldBe(expectedRows); }
            finally { await result.DisposeAsync(); }
            session.LastStatementMetrics!.JoinCandidatePairs.ShouldBe(other.LastStatementMetrics!.JoinCandidatePairs);
            session.LastStatementMetrics.RecordsExamined.ShouldBe(other.LastStatementMetrics.RecordsExamined);
        }
        Statements(engine.TempSpace!).ShouldBeEmpty();
    }

    private async Task<SqlDatabaseEngine> CreateFix2(string name, long budget, List<long>? calls = null, long? quota = null)
    {
        var builder = SqlDatabaseEngine.CreateBuilder(name);
        builder.Options.StatementMemoryBudget = budget;
        builder.Options.StatementTempSpaceQuota = quota;
        builder.Options.TempDirectory = Path.Combine(_root, name);
        builder.Functions.Add(SqlScalarFunction.Create("stamp", (long value) => { calls?.Add(value); return value; }));
        builder.Functions.Add(SqlScalarFunction.Create("fails_on", (long value) =>
        { calls?.Add(value); if (value == 317) { throw new InvalidOperationException("injected row failure"); } return value; }));
        return await builder.BuildAsync();
    }

    private static async Task SeedFix2(SqlDatabaseSession session, int count, int width, bool textKey = false, bool randomPayload = false)
    {
        var random = new Random(178906);
        foreach (string table in new[] { "jl", "jr" })
        {
            await session.ExecuteAsync($"CREATE TABLE {table}(id {(textKey ? "TEXT COLLATE case_insensitive" : "INT")},payload TEXT)");
            for (int start = 0; start < count; start += 100)
            {
                var values = new List<string>();
                for (int i = start; i < Math.Min(count, start + 100); i++)
                {
                    var bytes = new byte[width * 3 / 4]; random.NextBytes(bytes);
                    string payload = randomPayload ? Convert.ToBase64String(bytes) : new string('x', width);
                    values.Add($"({(textKey ? $"'{i}'" : i.ToString(System.Globalization.CultureInfo.InvariantCulture))},'{payload}')");
                }
                await session.ExecuteAsync($"INSERT INTO {table} VALUES" + string.Join(',', values));
            }
        }
    }

    private static async Task SeedFix2Owner(SqlDatabaseSession session, int count)
    {
        // Keep SeedJoinFix's physical history too: RecordsExamined includes versions.
        await session.ExecuteAsync("CREATE TABLE jl(id INT,payload TEXT)");
        await session.ExecuteAsync("CREATE TABLE jr(id INT,payload TEXT)");
        string payload = new('x', 512);
        for (int start = 0; start < count; start += 100)
        {
            await session.ExecuteAsync("INSERT INTO jl VALUES" + string.Join(',', Enumerable.Range(start, Math.Min(100, count - start)).Select(i => $"({i},'{payload}')")));
            await session.ExecuteAsync("INSERT INTO jr VALUES" + string.Join(',', Enumerable.Range(start, Math.Min(100, count - start)).Select(i => $"({i + 1000},'{payload}')")));
        }
        await session.ExecuteAsync("UPDATE jl SET id=NULL WHERE id%127=0");
        await session.ExecuteAsync("UPDATE jr SET id=NULL WHERE id%127=0");
    }

    private static string Fix2Fingerprint(IEnumerable<RowValues> rows)
    {
        string text = string.Join('\n', rows.Select(row => string.Join('|', row.Cells.Select(value => value is null
            ? "NULL" : value.GetType().FullName + ":" + Convert.ToString(value, CultureInfo.InvariantCulture)))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
