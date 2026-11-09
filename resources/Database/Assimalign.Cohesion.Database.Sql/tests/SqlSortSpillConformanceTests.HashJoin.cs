using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Hash join spill preserves probe and unmatched build order and work (#1789)")]
    [InlineData("INNER", 0)]
    [InlineData("LEFT", 0)]
    [InlineData("RIGHT", 0)]
    [InlineData("FULL", 0)]
    [InlineData("FULL", 2470)]
    public async Task ExecuteAsync_HashJoinBatches_ShouldPreserveOrderAndWork(string kind, int offset)
    {
        // Arrange
        await using var reference = SqlDatabaseEngine.Create("join-reference", new());
        await using var spilled = await Create("join-spilled", 262144);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await spilled.CreateDatabaseAsync("db")).CreateSessionAsync();
        await SeedHashJoin(a); await SeedHashJoin(b);
        string sql = $"SELECT l.id,r.id FROM jl l {kind} JOIN jr r ON l.id=r.id LIMIT 64 OFFSET {offset}";

        // Act
        await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        await using var actual = (DatabaseResultSet)await b.ExecuteAsync(sql, cancellationToken: CancellationToken.None);

        // Assert: compare ordered values, including the FULL left/build boundary.
        (await Rows(actual)).ShouldBe(await Rows(expected));
        var am = a.LastStatementMetrics!; var bm = b.LastStatementMetrics!;
        bm.JoinCandidatePairs.ShouldBe(am.JoinCandidatePairs);
        bm.RecordsExamined.ShouldBe(am.RecordsExamined);
        bm.HashMemoryBytes.ShouldBe(0); bm.PlainSelectMemoryBytes.ShouldBe(0);
        bm.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(262144);
        var metrics = SqlHashSpillMetrics.For(bm).ShouldNotBeNull();
        metrics.Runs.ShouldBeGreaterThan(0); metrics.PhysicalBytes.ShouldBeGreaterThan(0);
        await actual.DisposeAsync();
        TempBytes(spilled.TempSpace!).ShouldBe(0); Statements(spilled.TempSpace!).ShouldBeEmpty(); Files(spilled).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Hash join reports written batches when every probe key is NULL (#1789)")]
    public async Task ExecuteAsync_HashJoinNullProbes_ShouldReportSpilledBytes()
    {
        // Arrange
        await using var engine = await Create("join-null-probes", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await SeedHashJoin(session); await session.ExecuteAsync("UPDATE jl SET id=NULL");
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT r.id FROM jl l LEFT JOIN jr r ON l.id=r.id LIMIT 1");
        // Assert
        (await Rows(result)).ShouldHaveSingleItem().Cells[0].ShouldBeNull();
        var metrics = SqlHashSpillMetrics.For(session.LastStatementMetrics!).ShouldNotBeNull();
        metrics.PhysicalBytes.ShouldBeGreaterThan(0); metrics.PeakBytes.ShouldBeGreaterThan(0);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        await result.DisposeAsync();
        TempBytes(engine.TempSpace!).ShouldBe(0); Statements(engine.TempSpace!).ShouldBeEmpty(); Files(engine).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash join temporary failures and cancellation release all batches and keep the session online (#1789)")]
    [InlineData("CK05004")]
    [InlineData("CK05005")]
    [InlineData("cancel")]
    public async Task ExecuteAsync_HashJoinFailure_ShouldReleaseAndLocate(string code)
    {
        // Arrange
        await using var engine = await Create("join-failure", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await SeedHashJoin(session); await session.ExecuteAsync("BEGIN");
        using var cancellation = new CancellationTokenSource();
        bool injected = false;
        if (code == "cancel")
        { SqlPlanExecutor.ObserveHashSpill = (phase, _) => { if (phase == "join-partitioned") { injected = true; cancellation.Cancel(); } }; }
        else { BeforeRead(engine.TempSpace!, () => { injected = true; throw TempException(code, "injected join read failure"); }); }
        try
        {
            // Act / Assert
            if (code == "cancel")
            { await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync("SELECT l.id FROM jl l LEFT JOIN jr r ON l.id=r.id LIMIT 1", cancellationToken: cancellation.Token)); }
            else
            {
                var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT l.id FROM jl l LEFT JOIN jr r ON l.id=r.id LIMIT 1"));
                failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
                failure.Diagnostics[0].Start.ShouldNotBeNull(); failure.Diagnostics[0].End.ShouldNotBeNull();
            }
            injected.ShouldBeTrue();
            TempBytes(engine.TempSpace!).ShouldBe(0); Statements(engine.TempSpace!).ShouldBeEmpty(); Files(engine).ShouldBe(0);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        }
        finally { SqlPlanExecutor.ObserveHashSpill = null; BeforeRead(engine.TempSpace!, null); }
        (await session.ExecuteAsync("SELECT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
        await session.ExecuteAsync("COMMIT"); engine.OfflineDatabases.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Hash join predicate calls run once in the unchanged candidate order (#1789)")]
    public async Task ExecuteAsync_HashJoinPredicates_ShouldPreserveCallsAndFirstFailure()
    {
        // Arrange
        var calls = new[] { new List<long>(), new List<long>() };
        await using var reference = await Create("join-calls-reference", 67108864, calls[0]);
        await using var spilled = await Create("join-calls-spilled", 262144, calls[1]);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await spilled.CreateDatabaseAsync("db")).CreateSessionAsync();
        await SeedHashJoin(a); await SeedHashJoin(b);
        foreach (string function in new[] { "stamp", "fails_on" })
        {
            calls[0].Clear(); calls[1].Clear();
            string sql = $"SELECT l.id,r.id FROM jl l LEFT JOIN jr r ON l.id=r.id WHERE {function}(l.id-1000)>=0 LIMIT 32";
            // Act / Assert
            if (function == "stamp")
            {
                await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
                await using var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
                (await Rows(actual)).ShouldBe(await Rows(expected));
                b.LastStatementMetrics!.JoinCandidatePairs.ShouldBe(a.LastStatementMetrics!.JoinCandidatePairs);
            }
            else
            {
                var expected = await Should.ThrowAsync<DatabaseException>(async () => await a.ExecuteAsync(sql));
                var actual = await Should.ThrowAsync<DatabaseException>(async () => await b.ExecuteAsync(sql));
                actual.Diagnostics[0].Code.ShouldBe(expected.Diagnostics[0].Code);
                actual.Message.ShouldBe(expected.Message);
            }
            calls[1].ShouldBe(calls[0]); calls[1].ShouldNotBeEmpty();
            TempBytes(spilled.TempSpace!).ShouldBe(0); Statements(spilled.TempSpace!).ShouldBeEmpty(); Files(spilled).ShouldBe(0);
        }
    }
    [Fact(DisplayName = "Cohesion Test [Sql] - Two hash joins spill in one N-way statement and share one physical quota (#1789)")]
    public async Task ExecuteAsync_TwoHashJoins_ShouldShareQuotaAndPreserveOrder()
    {
        // Arrange
        await using var reference = SqlDatabaseEngine.Create("join-nway-reference", new());
        await using var spilled = await Create("join-nway-spilled", 1048576);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await spilled.CreateDatabaseAsync("db")).CreateSessionAsync();
        foreach (var session in new[] { a, b })
        {
            await SeedHashJoin(session);
            await session.ExecuteAsync("CREATE TABLE jt(id INT,payload TEXT)");
            string payload = new('y', 512);
            for (int start=1000; start<3500; start+=100)
            { await session.ExecuteAsync("INSERT INTO jt VALUES" + string.Join(',', Enumerable.Range(start,100).Select(i => $"({i},'{payload}')"))); }
        }
        int handoffs = 0;
        SqlPlanExecutor.ObserveHashSpill = (phase, _) =>
        { if (phase == "join-handoff") { handoffs++; Statements(spilled.TempSpace!).ShouldHaveSingleItem(); } };
        try
        {
            const string sql = "SELECT l.id,r.id,t.id FROM jl l LEFT JOIN jr r ON l.id=r.id LEFT JOIN jt t ON r.id=t.id LIMIT 64 OFFSET 2470";
            // Act
            await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
            await using var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
            // Assert
            (await Rows(actual)).ShouldBe(await Rows(expected)); handoffs.ShouldBe(2);
            b.LastStatementMetrics!.RecordsExamined.ShouldBe(a.LastStatementMetrics!.RecordsExamined);
            b.LastStatementMetrics.JoinCandidatePairs.ShouldBe(a.LastStatementMetrics.JoinCandidatePairs);
            b.LastStatementMetrics.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(1048576);
            await actual.DisposeAsync();
            b.LastStatementMetrics.HashMemoryBytes.ShouldBe(0); TempBytes(spilled.TempSpace!).ShouldBe(0); Statements(spilled.TempSpace!).ShouldBeEmpty();
        }
        finally { SqlPlanExecutor.ObserveHashSpill = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Hash join and aggregation share the same statement temp quota (#1789)")]
    public async Task ExecuteAsync_HashJoinAndGroup_ShouldShareQuotaAndPreserveResults()
    {
        // Arrange
        await using var reference = SqlDatabaseEngine.Create("join-group-reference", new());
        await using var spilled = await Create("join-group-spilled", 1048576);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await spilled.CreateDatabaseAsync("db")).CreateSessionAsync();
        await SeedHashJoin(a, 5000); await SeedHashJoin(b, 5000);
        int joins = 0, groups = 0;
        SqlPlanExecutor.ObserveHashSpill = (phase, _) =>
        {
            if (phase == "join-handoff") { joins++; Statements(spilled.TempSpace!).ShouldHaveSingleItem(); }
            if (phase == "input") { groups++; Statements(spilled.TempSpace!).ShouldHaveSingleItem(); }
        };
        try
        {
            const string sql = "SELECT l.id,COUNT(r.id) FROM jl l LEFT JOIN jr r ON l.id=r.id GROUP BY l.id ORDER BY l.id LIMIT 64";
            // Act
            await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
            await using var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
            // Assert
            (await Rows(actual)).ShouldBe(await Rows(expected)); joins.ShouldBe(1); groups.ShouldBeGreaterThan(0);
            b.LastStatementMetrics!.JoinCandidatePairs.ShouldBe(a.LastStatementMetrics!.JoinCandidatePairs);
            b.LastStatementMetrics.RecordsExamined.ShouldBe(a.LastStatementMetrics.RecordsExamined);
            b.LastStatementMetrics.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(1048576);
            await actual.DisposeAsync();
            b.LastStatementMetrics.HashMemoryBytes.ShouldBe(0); TempBytes(spilled.TempSpace!).ShouldBe(0); Statements(spilled.TempSpace!).ShouldBeEmpty(); Files(spilled).ShouldBe(0);
        }
        finally { SqlPlanExecutor.ObserveHashSpill = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Every hash join type completes ten grants under the default physical quota (#1789)")]
    [InlineData("INNER")]
    [InlineData("LEFT")]
    [InlineData("RIGHT")]
    [InlineData("FULL")]
    public async Task ExecuteAsync_TenHashJoinGrants_ShouldUseDefaultQuota(string kind)
    {
        // Arrange: 8,000 nine-cell build rows pay at least 384 bytes each, before arrays.
        const long grant = 262144;
        (8000L * 384).ShouldBeGreaterThan(10 * grant);
        var builder = SqlDatabaseEngine.CreateBuilder("join-default-quota");
        builder.Options.RootPath = System.IO.Path.Combine(_root, "join-default-quota");
        builder.Options.StatementMemoryBudget = grant;
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        foreach (string table in new[] { "jl", "jr" })
        {
            await session.ExecuteAsync($"CREATE TABLE {table}(id INT,a BOOLEAN,b BOOLEAN,c BOOLEAN,d BOOLEAN,e BOOLEAN,f BOOLEAN,g BOOLEAN,h BOOLEAN)");
            for (int start = 0; start < 8000; start += 200)
            {
                int shift = table == "jr" ? 1000 : 0;
                await session.ExecuteAsync($"INSERT INTO {table} VALUES" + string.Join(',', Enumerable.Range(start,200)
                    .Select(i => $"({i+shift},TRUE,FALSE,TRUE,FALSE,TRUE,FALSE,TRUE,FALSE)")));
            }
        }
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync($"SELECT l.id,r.id FROM jl l {kind} JOIN jr r ON l.id=r.id LIMIT 64");
        // Assert
        (await Rows(result)).Length.ShouldBe(64);
        var metrics = session.LastStatementMetrics!;
        SqlHashSpillMetrics.For(metrics).ShouldNotBeNull().Runs.ShouldBeGreaterThan(0);
        SqlHashSpillMetrics.For(metrics)!.PeakBytes.ShouldBeLessThanOrEqualTo(4 * grant);
        metrics.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(grant); metrics.HashMemoryBytes.ShouldBe(0);
        await result.DisposeAsync();
        TempBytes(engine.TempSpace!).ShouldBe(0); Statements(engine.TempSpace!).ShouldBeEmpty(); Files(engine).ShouldBe(0);
    }
    [Theory(DisplayName = "Cohesion Test [Sql] - Hash join keys preserve collated representatives, NaN, signed zero and decimal bits (#1789)")]
    [InlineData("DOUBLE PRECISION",0)]
    [InlineData("DECIMAL",1)]
    [InlineData("TEXT COLLATE case_insensitive",2)]
    public async Task ExecuteAsync_HashJoinTypedKeys_ShouldPreserveRepresentatives(string type, int family)
    {
        // Arrange
        await using var reference = SqlDatabaseEngine.Create("join-typed-reference",new());
        await using var spilled = await Create("join-typed-spilled",262144);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await spilled.CreateDatabaseAsync("db")).CreateSessionAsync();
        object left = family switch { 0 => double.NaN, 1 => 1.00m, _ => "ALICE" };
        object right = family switch { 0 => double.NaN, 1 => 1.0m, _ => "alice" };
        foreach (var session in new[] { a,b })
        {
            await SeedHashJoin(session);
            await session.ExecuteAsync($"ALTER TABLE jl ADD COLUMN k {type}");
            await session.ExecuteAsync($"ALTER TABLE jr ADD COLUMN k {type}");
            await session.ExecuteAsync("UPDATE jl SET k=@k",new Dictionary<string,object?>{["k"]=left});
            await session.ExecuteAsync("UPDATE jr SET k=@k",new Dictionary<string,object?>{["k"]=right});
        }
        string[] queries =
        [
            "SELECT l.id,r.id,l.k,r.k FROM jl l LEFT JOIN jr r ON l.id=r.id AND l.k=r.k LIMIT 64 OFFSET 990",
            "SELECT l.id,r.id FROM jl l FULL JOIN jr r ON l.id=r.id AND l.k=r.k ORDER BY l.id NULLS LAST,r.id",
        ];
        foreach (string sql in queries)
        {
            // Act
            await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
            await using var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
            // Assert: RowValues compares decimals and floats by representation bits.
            (await Rows(actual)).ShouldBe(await Rows(expected));
            b.LastStatementMetrics!.JoinCandidatePairs.ShouldBe(a.LastStatementMetrics!.JoinCandidatePairs);
            b.LastStatementMetrics.RecordsExamined.ShouldBe(a.LastStatementMetrics.RecordsExamined);
            b.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
        }
        if (family == 0)
        {
            foreach (var session in new[] { a,b })
            {
                await session.ExecuteAsync("UPDATE jl SET k=@k",new Dictionary<string,object?>{["k"]=-0.0d});
                await session.ExecuteAsync("UPDATE jr SET k=@k",new Dictionary<string,object?>{["k"]=0.0d});
            }
            const string sql = "SELECT l.k,r.k FROM jl l LEFT JOIN jr r ON l.id=r.id AND l.k=r.k LIMIT 64 OFFSET 1000";
            await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
            await using var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
            (await Rows(actual)).ShouldBe(await Rows(expected));
        }
        TempBytes(spilled.TempSpace!).ShouldBe(0); Statements(spilled.TempSpace!).ShouldBeEmpty(); Files(spilled).ShouldBe(0);
    }
    [Theory(DisplayName = "Cohesion Test [Sql] - Hash join quota and free-space floor refusals remain coded and located (#1789)")]
    [InlineData("CK05001")]
    [InlineData("CK05002")]
    [InlineData("CK05003")]
    public async Task ExecuteAsync_HashJoinQuota_ShouldRefuseAndRelease(string code)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("join-quota");
        builder.Options.TempDirectory = System.IO.Path.Combine(_root,".join-quota");
        builder.Options.StatementMemoryBudget = 262144;
        if (code == "CK05001") { builder.Options.TempSpaceQuota=0; }
        if (code == "CK05002") { builder.Options.StatementTempSpaceQuota=0; }
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await SeedHashJoin(session);
        if (code == "CK05003") { Available(engine.TempSpace!)=()=>0; ProbeTick(engine.TempSpace!)=0; }
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("SELECT l.id FROM jl l LEFT JOIN jr r ON l.id=r.id LIMIT 1"));
        // Assert
        failure.Diagnostics[0].Code.ShouldBe(code); failure.Diagnostics[0].Start.ShouldNotBeNull(); failure.Diagnostics[0].End.ShouldNotBeNull();
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        TempBytes(engine.TempSpace!).ShouldBe(0); Statements(engine.TempSpace!).ShouldBeEmpty(); Files(engine).ShouldBe(0); engine.OfflineDatabases.ShouldBeEmpty();
        (await session.ExecuteAsync("SELECT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
    }
    [Theory(DisplayName = "Cohesion Test [Sql] - In-memory hash join spill requires explicit temp permission (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_HashJoinMemoryEngine_ShouldHonorTempPermission(bool temp)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("join-memory-permission");
        builder.Options.StatementMemoryBudget=262144; builder.Options.StatementTempSpaceQuota=67108864;
        if (temp) { builder.Options.TempDirectory=System.IO.Path.Combine(_root,".join-memory"); }
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await SeedHashJoin(session);
        const string sql="SELECT l.id,r.id FROM jl l LEFT JOIN jr r ON l.id=r.id LIMIT 32";
        // Act / Assert
        if (!temp)
        {
            var failure=await Should.ThrowAsync<DatabaseException>(async()=>await session.ExecuteAsync(sql));
            failure.Diagnostics[0].Code.ShouldBe("CS05067"); SqlHashSpillMetrics.For(session.LastStatementMetrics!).ShouldBeNull();
        }
        else
        {
            await using var result=(DatabaseResultSet)await session.ExecuteAsync(sql);
            (await Rows(result)).Length.ShouldBe(32);
            SqlHashSpillMetrics.For(session.LastStatementMetrics!).ShouldNotBeNull().Runs.ShouldBeGreaterThan(0);
            await using var explained=(DatabaseResultSet)await session.ExecuteAsync("EXPLAIN ANALYZE "+sql);
            string detail=string.Join(' ',(await Rows(explained)).SelectMany(r=>r.Cells).OfType<string>());
            detail.ShouldContain("hash_join_probe_chunk_reads=",Case.Sensitive); detail.ShouldContain("spill_merge_passes=",Case.Sensitive);
            detail.ShouldContain("spilled_bytes=",Case.Sensitive);
            await result.DisposeAsync(); await explained.DisposeAsync();
            TempBytes(engine.TempSpace!).ShouldBe(0); Statements(engine.TempSpace!).ShouldBeEmpty(); Files(engine).ShouldBe(0);
        }
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
    }
    private static async Task SeedHashJoin(SqlDatabaseSession session, int count = 2500)
    {
        await session.ExecuteAsync("CREATE TABLE jl(id INT,payload TEXT)");
        await session.ExecuteAsync("CREATE TABLE jr(id INT,payload TEXT)");
        string payload = new('x', 512);
        for (int start = 0; start < count; start += 100)
        {
            string rows = string.Join(',', Enumerable.Range(start, Math.Min(100, count-start)).Select(i => $"({i},'{payload}')"));
            string right = string.Join(',', Enumerable.Range(start, Math.Min(100, count-start)).Select(i => $"({i+1000},'{payload}')"));
            await session.ExecuteAsync("INSERT INTO jl VALUES" + rows);
            await session.ExecuteAsync("INSERT INTO jr VALUES" + right);
        }
        await session.ExecuteAsync("UPDATE jl SET id=NULL WHERE id%127=0");
        await session.ExecuteAsync("UPDATE jr SET id=NULL WHERE id%127=0");
    }
}
