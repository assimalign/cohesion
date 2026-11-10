using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - A failed shared collector preserves its first temp error while the join drains (#1789)")]
    public async Task ExecuteAsync_JoinFixCollectorFailure_ShouldKeepFirstError()
    {
        // Arrange
        await using var engine = await Create("fix-collector-failure", 1048576);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await SeedJoinFix(session, 5000);
        bool injected = false;
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (phase == "append" && !injected) { injected = true; throw TempException("CK05005", "injected collector failure"); }
        };
        try
        {
            // Act / Assert
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync("SELECT l.id,r.payload FROM jl l LEFT JOIN jr r ON l.id=r.id ORDER BY r.id"));
            injected.ShouldBeTrue(); failure.Diagnostics[0].Code.ShouldBe("CK05005");
            Statements(engine.TempSpace!).ShouldBeEmpty();
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        }
        finally { SqlRowCollector.ObserveSpill = null; }
    }
    [Fact(DisplayName = "Cohesion Test [Sql] - Deferred heap drain keeps ties under a shared grant (#1789)")]
    public async Task ExecuteAsync_JoinFixHeapPressure_ShouldKeepOrder()
    {
        // Arrange: a second operator temporarily owns the remaining grant during a
        // wider top-N replacement, then releases it before the FULL unmatched phase.
        await using var reference = SqlDatabaseEngine.Create("fix-heap-reference", new());
        await using var engine = await Create("fix-heap", 1048576);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await SeedJoinFix(a, 2500); await SeedJoinFix(b, 2500);
        const string sql = "SELECT l.id,r.payload FROM jl l FULL JOIN jr r ON l.id=r.id ORDER BY r.id IS NULL LIMIT 600";
        await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
        int matches = 0; long held = 0;
        SqlPlanExecutor.FullJoinProgress = (phase, metrics, _) =>
        {
            if (phase != "match") { return; }
            if (++matches == 100) { held = 1048576 - metrics.HashMemoryBytes; metrics.ReserveHashMemory(held, 1048576); }
            else if (held != 0) { metrics.ReleaseHashMemory(held); held = 0; }
        };
        try
        {
            // Act / Assert
            var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
            try { (await Rows(actual)).ShouldBe(await Rows(expected)); }
            finally { await actual.DisposeAsync(); }
        }
        finally
        {
            SqlPlanExecutor.FullJoinProgress = null;
            if (held != 0) { b.LastStatementMetrics!.ReleaseHashMemory(held); }
        }
        Statements(engine.TempSpace!).ShouldBeEmpty();
    }
    [Theory(DisplayName = "Cohesion Test [Sql] - FULL deferred top-N keeps complete stable ties (#1789)")]
    [InlineData(262144, 60)]
    [InlineData(262144, 37)]
    [InlineData(1048576, 60)]
    [InlineData(1048576, 37)]
    [InlineData(1048576, 600)]
    public async Task ExecuteAsync_JoinFixDeferredTopN_ShouldKeepOrder(long budget, int limit)
    {
        // Arrange
        await using var reference = SqlDatabaseEngine.Create("fix-reference", new());
        await using var engine = await Create("fix-full", budget);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await SeedJoinFix(a, 2500); await SeedJoinFix(b, 2500);
        string sql = $"SELECT l.id, r.payload FROM jl l FULL JOIN jr r ON l.id = r.id ORDER BY r.id IS NULL LIMIT {limit} OFFSET 13";
        // Act
        await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
        bool deferred = false; int matches = 0; long held = 0;
        SqlStatementMetrics? ledger = null;
        SqlPlanExecutor.FullJoinProgress = (phase, metrics, _) =>
        {
            if (phase != "match") { return; }
            if (++matches == 30)
            { ledger = metrics; held = budget - metrics.HashMemoryBytes; metrics.ReserveHashMemory(held, budget); }
            else if (held != 0) { metrics.ReleaseHashMemory(held); held = 0; }
        };
        SqlRowCollector.ObserveSpill = phase => { if (phase == "deferred") { deferred = true; } };
        try
        {
            var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
            try { (await Rows(actual)).ShouldBe(await Rows(expected)); }
            finally { await actual.DisposeAsync(); }
        }
        finally
        {
            SqlPlanExecutor.FullJoinProgress = null; SqlRowCollector.ObserveSpill = null;
            if (held != 0) { ledger!.ReleaseHashMemory(held); }
        }
        // Assert
        deferred.ShouldBeTrue(); SqlSortSpillMetrics.For(b.LastStatementMetrics!).ShouldNotBeNull();
        Statements(engine.TempSpace!).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join collectors release their exact temp owners over repeated statements (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_JoinFixCollectorOwnership_ShouldRelease(bool materialize)
    {
        // Arrange
        var referenceBuilder = SqlDatabaseEngine.CreateBuilder("fix-owner-reference");
        referenceBuilder.Functions.Add(SqlScalarFunction.Create("stamp", (long value) => value));
        await using var reference = await referenceBuilder.BuildAsync();
        await using var engine = await Create("fix-owner", 1048576, new List<long>());
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await SeedJoinFix(a, materialize ? 5000 : 1800); await SeedJoinFix(b, materialize ? 5000 : 1800);
        string sql = materialize ? "SELECT stamp(l.id), r.id FROM jl l JOIN jr r ON l.id=r.id ORDER BY r.id"
            : $"SELECT l.id, COUNT(r.id) AS c, '{new string('x', 500)}' AS pad FROM jl l LEFT JOIN jr r ON l.id=r.id GROUP BY l.id ORDER BY c DESC, l.id";
        await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
        var rows = await Rows(expected);
        for (int i = 0; i < 20; i++)
        {
            // Act
            var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
            try { (await Rows(actual)).ShouldBe(rows); }
            finally { await actual.DisposeAsync(); }
            // Assert
            Statements(engine.TempSpace!).ShouldBeEmpty();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Two FULL producers release their deferred grants before ordered delivery (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_JoinFixTwoFull_ShouldKeepOrder(bool nestedRight)
    {
        // Arrange
        await using var reference = SqlDatabaseEngine.Create("fix-two-full-reference", new());
        await using var engine = await Create("fix-two-full", 1048576);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        foreach (var session in new[] { a, b })
        {
            await SeedJoinFix(session, 2500);
            await session.ExecuteAsync("CREATE TABLE jt(id INT,payload TEXT)");
            for (int start = 1000; start < 3500; start += 100)
            {
                await session.ExecuteAsync("INSERT INTO jt VALUES" + string.Join(',', Enumerable.Range(start, 100)
                    .Select(i => $"({i},'{new string('x', 512)}')")));
            }
        }
        string sql = nestedRight
            ? "SELECT l.id,r.id,t.payload FROM jl l FULL JOIN (jr r FULL JOIN jt t ON r.id=t.id) ON l.id=r.id ORDER BY t.id IS NULL LIMIT 600"
            : "SELECT l.id,r.id,t.payload FROM jl l FULL JOIN jr r ON l.id=r.id FULL JOIN jt t ON r.id=t.id ORDER BY t.id IS NULL LIMIT 600";
        // Act / Assert
        await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
        var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
        try { (await Rows(actual)).ShouldBe(await Rows(expected)); }
        finally { await actual.DisposeAsync(); }
        Statements(engine.TempSpace!).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Random probe I/O scales linearly with indexed chunks (#1789)")]
    public async Task ExecuteAsync_JoinFixRandomProbes_ShouldReadLinearChunks()
    {
        // Arrange / Act
        var small = await Measure(800); var large = await Measure(3200);
        _output.WriteLine($"JOIN_LINEAR|N=800|bytes={small.Bytes}|4N=3200|bytes={large.Bytes}|ratio={(double)large.Bytes / small.Bytes}|chunks={small.Chunks},{large.Chunks}");
        // Assert
        large.Bytes.ShouldBeLessThanOrEqualTo(5 * small.Bytes);
        small.Bytes.ShouldBeLessThanOrEqualTo(800 * 8192L);
        large.Bytes.ShouldBeLessThanOrEqualTo(3200 * 8192L);
        if (small.Chunks > 0 && large.Chunks > 0) { large.Chunks.ShouldBeLessThanOrEqualTo(5 * small.Chunks); }

        async Task<(long Bytes, long Chunks)> Measure(int count)
        {
            await using var engine = await Create("fix-linear-" + count, 262144);
            await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
            await session.ExecuteAsync("CREATE TABLE jl(id INT,payload TEXT)");
            await session.ExecuteAsync("CREATE TABLE jr(id INT,payload TEXT)");
            int[] keys = Enumerable.Range(0, count).ToArray(); new Random(178906).Shuffle(keys);
            for (int start = 0; start < count; start += 100)
            {
                int size = Math.Min(100, count - start); string payload = new('x', 512);
                await session.ExecuteAsync("INSERT INTO jl VALUES" + string.Join(',', Enumerable.Range(start, size).Select(i => $"({keys[i]},'{payload}')")));
                await session.ExecuteAsync("INSERT INTO jr VALUES" + string.Join(',', Enumerable.Range(start, size).Select(i => $"({i},'{payload}')")));
            }
            long reads = 0;
            BeforeRead(engine.TempSpace!, () => reads++);
            var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT l.id,r.id FROM jl l LEFT JOIN jr r ON l.id=r.id LIMIT 1");
            await result.DisposeAsync(); BeforeRead(engine.TempSpace!, null);
            session.LastStatementMetrics!.JoinCandidatePairs.ShouldBe(count);
            var metrics = SqlHashSpillMetrics.For(session.LastStatementMetrics!)!;
            long bytes = (long)(typeof(SqlHashSpillMetrics).GetProperty("ProbeBytesRead", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(metrics) ?? reads);
            var chunks = typeof(SqlHashSpillMetrics).GetProperty("ProbeChunkReads", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(metrics);
            _output.WriteLine($"JOIN_LINEAR_SAMPLE|N={count}|page_reads={reads}|probe_bytes={bytes}|probe_chunks={chunks}");
            Statements(engine.TempSpace!).ShouldBeEmpty();
            // Every unique hash covers one chunk; each chunk contains at most one page
            // of encoded records in this fixture, and a cache hit performs no read.
            if (chunks is long chunkReads) { chunkReads.ShouldBeLessThanOrEqualTo(count); }
            return (bytes, chunks is long measured ? measured : -1);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Join fitting async state machine fields match base (#1789)")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The required layout probe reflects compiler-generated types in this untrimmed test assembly.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The required layout probe enumerates the compiler's private state-machine fields in an untrimmed test assembly.")]
    public void ExecuteAsync_JoinFixLayouts_ShouldMatchBase()
    {
        // Arrange / Act
        foreach (string name in new[] { "EnumerateFilteredRetentionTreeAsync", "ExecuteSpillRootAsync" })
        {
            var type = typeof(SqlPlanExecutor).GetNestedTypes(BindingFlags.NonPublic).Single(t => t.Name.StartsWith("<" + name + ">", StringComparison.Ordinal));
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            // Assert: report every field so the two compiler layouts are reviewable.
            _output.WriteLine($"JOIN_LAYOUT|{name}|count={fields.Length}|{string.Join(',', fields.Select(f => f.Name))}");
#if !DEBUG
            fields.Length.ShouldBe(name == "ExecuteSpillRootAsync" ? 16 : 26);
#else
            fields.Length.ShouldBe(name == "ExecuteSpillRootAsync" ? 37 : 30);
#endif
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash groups spanning at least five chunks stream skew in every join type (#1789)")]
    [InlineData("INNER")]
    [InlineData("LEFT")]
    [InlineData("RIGHT")]
    [InlineData("FULL")]
    public async Task ExecuteAsync_JoinFixSkewChunks_ShouldCompleteInOrder(string kind)
    {
        // Arrange
        await using var reference = SqlDatabaseEngine.Create("fix-skew-reference", new());
        await using var engine = await Create("fix-skew", 262144);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        foreach (var session in new[] { a, b })
        {
            foreach (string table in new[] { "jl", "jr" })
            {
                await session.ExecuteAsync($"CREATE TABLE {table}(id INT,payload TEXT,original_id INT)");
                await session.ExecuteAsync($"INSERT INTO {table} VALUES" + string.Join(',', Enumerable.Range(0, 24)
                    .Select(i => $"(1,'{new string('x', 5800)}',{i})")));
            }
        }
        // Both possible builds exceed G. Compare all 576 outputs, including the build
        // ordinal on each side of every chunk switch; the projected result fits G.
        string build = kind == "RIGHT" ? "l" : "r";
        string sql = $"SELECT {build}.original_id FROM jl l {kind} JOIN jr r ON l.id=r.id";
        // Act / Assert
        await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
        long span = 0;
        SqlPlanExecutor.ObserveHashSpill = (phase, value) => { if (phase == "join-index-chunks") { span = value; } };
        DatabaseResultSet actual;
        try { actual = (DatabaseResultSet)await b.ExecuteAsync(sql); }
        finally { SqlPlanExecutor.ObserveHashSpill = null; }
        try
        {
            var rows = await Rows(actual); rows.Length.ShouldBe(576);
            rows.ShouldBe(await Rows(expected));
        }
        finally { await actual.DisposeAsync(); }
        b.LastStatementMetrics!.JoinCandidatePairs.ShouldBe(a.LastStatementMetrics!.JoinCandidatePairs);
        b.LastStatementMetrics.RecordsExamined.ShouldBe(a.LastStatementMetrics.RecordsExamined);
        var spill = SqlHashSpillMetrics.For(b.LastStatementMetrics).ShouldNotBeNull();
        var chunks = typeof(SqlHashSpillMetrics).GetProperty("ProbeChunkReads", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(spill);
        chunks.ShouldNotBeNull(); ((long)chunks).ShouldBeGreaterThanOrEqualTo(24 * 5);
        span.ShouldBeGreaterThanOrEqualTo(5);
        Statements(engine.TempSpace!).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Complete nonunique join differentials include matched prefixes and all switches (#1789)")]
    [InlineData("INNER")]
    [InlineData("LEFT")]
    [InlineData("RIGHT")]
    [InlineData("FULL")]
    public async Task ExecuteAsync_JoinFixCompleteResults_ShouldPreserveEveryRow(string kind)
    {
        // Arrange
        await using var reference = SqlDatabaseEngine.Create("fix-all-reference", new());
        await using var engine = await Create("fix-all", 1048576);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        foreach (var session in new[] { a, b })
        {
            foreach (string table in new[] { "jl", "jr" })
            {
                await session.ExecuteAsync($"CREATE TABLE {table}(id INT,payload TEXT,original_id INT)");
                for (int start = 0; start < 1200; start += 100)
                {
                    await session.ExecuteAsync($"INSERT INTO {table} VALUES" + string.Join(',', Enumerable.Range(start, 100)
                        .Select(i => $"({(i % 127 == 0 ? "NULL" : (i % 400).ToString(System.Globalization.CultureInfo.InvariantCulture))},'{new string('x', 512)}',{i})")));
                }
            }
        }
        string sql = $"SELECT l.original_id,r.original_id FROM jl l {kind} JOIN jr r ON l.id=r.id";
        // Act / Assert
        await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
        var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
        try { (await Rows(actual)).ShouldBe(await Rows(expected)); }
        finally { await actual.DisposeAsync(); }
        b.LastStatementMetrics!.JoinCandidatePairs.ShouldBe(a.LastStatementMetrics!.JoinCandidatePairs);
        b.LastStatementMetrics.RecordsExamined.ShouldBe(a.LastStatementMetrics.RecordsExamined);
        Statements(engine.TempSpace!).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Large evaluated probe cells never depend on cache residency (#1789)")]
    public async Task ExecuteAsync_JoinFixWideProbe_ShouldAlwaysComplete()
    {
        // Arrange
        await using var engine = await Create("fix-wide-probe", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await SeedJoinFix(session, 400);
        await session.ExecuteAsync("UPDATE jl SET payload='" + new string('x', 7000) + "' WHERE id=1");
        _output.WriteLine($"JOIN_WIDE_PROCESS|pid={Environment.ProcessId}|initial_prefix={unchecked((uint)JoinFixHash(1L)) & 7}");
        // Act / Assert
        var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT l.id,r.id FROM jl l LEFT JOIN jr r ON l.id=r.id LIMIT 1");
        await result.DisposeAsync(); Statements(engine.TempSpace!).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Two joins hand off when the remaining grant fills before the cache target (#1789)")]
    public async Task ExecuteAsync_JoinFixRemainingGrant_ShouldSpill()
    {
        // Arrange
        await using var reference = SqlDatabaseEngine.Create("fix-remaining-reference", new());
        await using var engine = await Create("fix-remaining", 262144);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        foreach (var session in new[] { a, b })
        {
            await SeedJoinFix(session, 430);
            await session.ExecuteAsync("UPDATE jl SET id=id+1000");
            await session.ExecuteAsync("CREATE TABLE jt(id INT,payload TEXT)");
            for (int start = 0; start < 430; start += 100)
            {
                await session.ExecuteAsync("INSERT INTO jt VALUES" + string.Join(',', Enumerable.Range(start, Math.Min(100, 430 - start))
                    .Select(i => $"({i + 1000},'{new string('x', 512)}')")));
            }
        }
        const string sql = "SELECT l.id,r.id,t.id FROM jl l LEFT JOIN jr r ON l.id=r.id LEFT JOIN jt t ON r.id=t.id LIMIT 64";
        // Act / Assert
        await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
        var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
        try { (await Rows(actual)).ShouldBe(await Rows(expected)); }
        finally { await actual.DisposeAsync(); }
        Statements(engine.TempSpace!).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Build-time skew returns real matches across indexed chunks (#1789)")]
    public async Task ExecuteAsync_JoinFixBuildSkew_ShouldComplete()
    {
        // Arrange: the later duplicate group matches jl's non-NULL key 1.
        await using var reference = SqlDatabaseEngine.Create("fix-build-skew-reference", new());
        await using var engine = await Create("fix-build-skew", 262144);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        foreach (var session in new[] { a, b })
        {
            await SeedJoinFix(session, 800);
            await session.ExecuteAsync("UPDATE jr SET id=1 WHERE id>=1400");
        }
        // Act / Assert
        const string sql = "SELECT l.id,r.id FROM jl l LEFT JOIN jr r ON l.id=r.id LIMIT 64";
        await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
        var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
        try
        {
            var rows = await Rows(actual); rows.ShouldBe(await Rows(expected));
            rows.ShouldContain(row => row.Cells[1] != null);
        }
        finally { await actual.DisposeAsync(); }
        Statements(engine.TempSpace!).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Indexed chunks fit the working grant left by another operator (#1789)")]
    public async Task ExecuteAsync_JoinFixHeldGrant_ShouldSpillWithinRemainder()
    {
        // Arrange
        await using var reference = SqlDatabaseEngine.Create("fix-held-reference", new());
        await using var engine = await Create("fix-held", 1048576);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await SeedJoinFix(a, 2500); await SeedJoinFix(b, 2500);
        const string sql = "SELECT l.id,r.id FROM jl l FULL JOIN jr r ON l.id=r.id LIMIT 1";
        await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
        SqlStatementMetrics? held = null;
        bool reserved = false;
        SqlPlanExecutor.FullJoinProgress = (phase, metrics, _) =>
        {
            if (phase == "build" && !reserved)
            { metrics.ReserveHashMemory(800000, 1048576); held = metrics; reserved = true; }
        };
        SqlPlanExecutor.ObserveHashSpill = (phase, _) =>
        { if (phase == "join-partitioned" && held is not null) { held.ReleaseHashMemory(800000); held = null; } };
        try
        {
            // Act / Assert
            var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
            try { (await Rows(actual)).ShouldBe(await Rows(expected)); }
            finally { await actual.DisposeAsync(); }
            reserved.ShouldBeTrue();
        }
        finally
        {
            SqlPlanExecutor.FullJoinProgress = null; SqlPlanExecutor.ObserveHashSpill = null;
            held?.ReleaseHashMemory(800000);
        }
        b.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        Statements(engine.TempSpace!).ShouldBeEmpty();
    }

    private static int JoinFixHash(long value)
    {
        var hash = new HashCode();
        hash.Add(SqlValueComparer.GetHashCode(value, Assimalign.Cohesion.Database.Types.Collation.Binary));
        return hash.ToHashCode();
    }

    private static async Task SeedJoinFix(SqlDatabaseSession session, int count)
    {
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
}
