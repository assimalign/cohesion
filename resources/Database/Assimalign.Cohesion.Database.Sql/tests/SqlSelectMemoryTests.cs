using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed partial class SqlSelectMemoryTests
{
    private readonly ITestOutputHelper _output;

    public SqlSelectMemoryTests(ITestOutputHelper output) { _output = output; }

    public static IEnumerable<object[]> Buffers()
    {
        string[] queries =
        [
            "SELECT k FROM t",
            "SELECT k FROM t ORDER BY k",
            "SELECT k FROM t ORDER BY k LIMIT 1",
            "SELECT DISTINCT k FROM t",
            "SELECT COUNT(*) FROM t",
            "SELECT k,COUNT(*) FROM t GROUP BY k",
            "SELECT COUNT(DISTINCT k) FROM t",
            "SELECT STRING_AGG(CAST(v AS TEXT),',' ORDER BY v) FROM t",
            "SELECT t.k FROM t CROSS JOIN u",
            // The root seeks into a: only b is buffered; fresh a.note cells pay in output.
            "SELECT a.note FROM a RIGHT JOIN b ON a.id=b.aid",
            "SELECT t.k FROM t FULL JOIN u ON t.k=u.k",
            "WITH c AS (SELECT k FROM t) SELECT COUNT(*) FROM c",
            "SELECT COUNT(*) FROM (SELECT k FROM t) d",
            "SELECT COUNT(*) FROM view_t",
            "SELECT (SELECT COUNT(*) FROM t) FROM u",
            "SELECT k,(SELECT COUNT(*) FROM t WHERE t.k=u.k) FROM u",
            "SELECT k FROM t WHERE k IN (SELECT k FROM u)",
            "SELECT k FROM t WHERE k IN (SELECT u.k FROM u WHERE u.k=t.k)",
            "SELECT k FROM t UNION ALL SELECT k FROM u",
            "SELECT k FROM t UNION SELECT k FROM u",
            "SELECT k FROM t INTERSECT SELECT k FROM u",
            "SELECT k FROM t EXCEPT ALL SELECT k FROM u",
            "SELECT record_value(k),k FROM t ORDER BY k",
            "SELECT MIN(note) FROM a",
            "SELECT MAX(note) FROM a",
            "SELECT STRING_AGG(CAST(v AS TEXT),',') FROM t",
            "SELECT DISTINCT t.k FROM t CROSS JOIN u",
            "SELECT t.k FROM t CROSS JOIN u ORDER BY t.k",
            "SELECT STRING_AGG(DISTINCT note,',' ORDER BY note) FROM spell",
            "SELECT nextval('s'),(SELECT u.k FROM u WHERE u.k=t.k) FROM t LIMIT 2",
            "SELECT nextval('s') AS id,(SELECT note FROM wide WHERE wide.k=t.k) AS note,t.k FROM t ORDER BY t.k",
            "SELECT (SELECT COUNT(*) FROM t),COUNT(*) OVER() FROM u",
            "SELECT (SELECT COUNT(*) OVER() FROM (SELECT k FROM t) d LIMIT 1) AS c,u.k FROM u ORDER BY u.k",
            "SELECT (SELECT CASE WHEN COUNT(*) OVER()>0 THEN (SELECT COUNT(*) FROM t) ELSE 0 END FROM u LIMIT 1),u.k FROM u ORDER BY u.k",
            "SELECT k,(SELECT COUNT(*) FROM (SELECT * FROM t WHERE t.k=u.k) d1 JOIN (SELECT * FROM t WHERE t.k=u.k) d2 ON d1.v=d2.v) FROM u",
            "SELECT t.k,COUNT(*),(SELECT a.note FROM a WHERE a.id=t.k) FROM t GROUP BY ROLLUP(t.k) ORDER BY (SELECT a.id FROM a WHERE a.id=t.k)",
            "SELECT nextval('s'),k FROM t GROUP BY ROLLUP(k) ORDER BY k",
        ];
        long[][] charges =
        [
            [288, 192, 288, 192],
            [480, 320, 480, 320],
            [160, 0, 160, 0],
            [448, 352, 544, 416],
            [352, 256, 760, 664],
            [976, 840, 1384, 1248],
            [960, 864, 1368, 1272],
            [2016, 1840, 2424, 2248],
            [768, 672, 1632, 1536],
            [600, 536, 944, 832],
            [1392, 1296, 1632, 1456],
            [640, 576, 928, 864],
            [832, 736, 928, 864],
            [832, 736, 928, 864],
            [352, 256, 760, 664],
            [488, 392, 624, 528],
            [424, 328, 424, 328],
            [384, 328, 384, 328],
            [960, 864, 960, 864],
            [736, 640, 736, 640],
            [1376, 1280, 1376, 1280],
            [928, 832, 928, 832],
            [1224, 1200, 1224, 1200],
            [368, 264, 648, 544],
            [352, 264, 632, 544],
            [370, 266, 778, 674],
            [640, 544, 1888, 1760],
            [1152, 992, 2016, 1856],
            [1432, 1256, 1736, 1560],
            [368, 272, 368, 272],
            [17328, 17192, 17328, 17192],
            [4272, 4248, 4272, 4248],
            [4676, 4652, 4676, 4652],
            [4176, 4152, 4176, 4152],
            [1480, 1376, 1656, 1440],
            [5332, 5132, 5332, 5132],
            [3868, 3732, 3868, 3732],
        ];
        for (int i = 0; i < queries.Length; i++)
        {
            yield return [queries[i], false, charges[i][0], charges[i][1]];
            yield return [queries[i], true, charges[i][2], charges[i][3]];
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: each retained buffer refuses at its exact boundary and releases (#1776)")]
    [MemberData(nameof(Buffers))]
    public async Task ExecuteAsync_RetainedBuffer_ShouldReserveAndRelease(string sql, bool forceBase, long expectedPeak, long expectedRefusal)
    {
        // Arrange / Act: measure a deterministic charge, then test both sides of that boundary.
        long peak = await Run(1_048_576, false);
        long refusedPeak = await Run(peak - 1, true);
        _output.WriteLine($"BUFFER|{forceBase}|{sql}|{peak}|{refusedPeak}");
        await Run(peak, false);
        peak.ShouldBe(expectedPeak);
        refusedPeak.ShouldBe(expectedRefusal);

        // Assert: every admission site also unwinds cancellation and another failure.
        await VerifyEveryUnwind(sql, forceBase);

        async Task<long> Run(long budget, bool refuses)
        {
            var builder = SqlDatabaseEngine.CreateBuilder("select-buffer");
            builder.Options.StatementMemoryBudget = budget;
            builder.Functions.Add(SqlScalarFunction.Create("record_value", (long value) => value));
            await using var engine = await builder.BuildAsync(CancellationToken.None);
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await Setup(session);
            database.Definitions.ForceSelectRetentionBasePath = forceBase;
            try
            {
                if (refuses)
                {
                    var failure = await Should.ThrowAsync<DatabaseException>(async () => await Read(session, sql));
                    failure.Diagnostics.ShouldContain(item => item.Code == "CS05067");
                }
                else { await Read(session, sql); }
                return session.LastStatementMetrics.ShouldNotBeNull().PeakHashMemoryBytes;
            }
            finally
            {
                session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
                session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SELECT memory: large scans retain bounded COUNT and low cardinality groups (#1776)")]
    public async Task ExecuteAsync_LargeInput_ShouldChargeOnlyRetainedGroups()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("bounded-groups");
        builder.Options.StatementMemoryBudget = 8192;
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Repeat("(1)", 5000)), cancellationToken: CancellationToken.None);

        // Act / Assert
        foreach (string sql in new[] { "SELECT COUNT(*) FROM t", "SELECT k,COUNT(*) FROM t GROUP BY k" })
        {
            await Read(session, sql);
            var metrics = session.LastStatementMetrics.ShouldNotBeNull();
            metrics.RecordsExamined.ShouldBe(5000);
            metrics.PeakHashMemoryBytes.ShouldBe(sql == "SELECT COUNT(*) FROM t" ? 352 : 488);
            metrics.HashMemoryBytes.ShouldBe(0);
            _output.WriteLine($"BOUNDED|{sql}|{metrics.PeakHashMemoryBytes}");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CTAS memory: kept rows have identical admission regardless of capacity (#1762)")]
    public void ReserveMemory_KeptRows_ShouldIgnoreDiscardedCapacity()
    {
        // Arrange
        var narrow = new SqlMaterializedResultSet([], [new object?[] { 1L, 1000 }]);
        var wideRows = new List<object?[]>(4096) { new object?[] { 1L, 1000 } };
        var wide = new SqlMaterializedResultSet([], wideRows);
        var metrics = new SqlStatementMetrics();

        // Act
        long narrowBytes = narrow.ReserveMemory(metrics, 8192, CancellationToken.None);
        metrics.ReleaseHashMemory(narrowBytes);
        long wideBytes = wide.ReserveMemory(metrics, 8192, CancellationToken.None);
        metrics.ReleaseHashMemory(wideBytes);

        // Assert
        narrowBytes.ShouldBe(136);
        wideBytes.ShouldBe(narrowBytes);
        metrics.PeakHashMemoryBytes.ShouldBe(136);
        metrics.HashMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CTAS memory: sequence OFFSET source admission follows kept rows (#1762)")]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, true, true)]
    public async Task ExecuteAsync_SequenceOffset_ShouldAdmitOnlyKeptRows(bool grouped, bool forceBase, bool sequence, bool temp)
    {
        // Arrange: source buffers charge normally; CTAS admission counts only kept output.
        var builder = SqlDatabaseEngine.CreateBuilder("ctas-offset");
        if (temp) { builder.Options.TempDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ctas-pins-" + Guid.NewGuid().ToString("N")); }
        builder.Options.StatementMemoryBudget = 1_048_576;
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE SEQUENCE s", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(0, 1001).Select(k => $"({k})")), cancellationToken: CancellationToken.None);
        database.Definitions.ForceSelectRetentionBasePath = forceBase;
        var admitted = new List<long>();
        string expression = sequence ? "nextval('s')" : "CAST(k AS BIGINT)";
        try
        {
            SqlPlanExecutor.TableAsSourceAdmitted = admitted.Add;

            // Act
            await session.ExecuteAsync($"CREATE TABLE p AS SELECT {expression} AS id,k FROM t " +
                (grouped ? "GROUP BY k ORDER BY k " : "") + "LIMIT 1 OFFSET 1000", cancellationToken: CancellationToken.None);
            var metrics = session.LastStatementMetrics.ShouldNotBeNull();
            _output.WriteLine($"CTAS|{grouped}|base={forceBase}|{expression}|peak={metrics.PeakHashMemoryBytes}");

            // Assert
            metrics.HashMemoryBytes.ShouldBe(0);
            metrics.PeakHashMemoryBytes.ShouldBe(grouped ? sequence ? forceBase ? 664664 : 568568 : forceBase ? 520520 : 424424 : 743);
            admitted.ShouldBe([136L]);
            await using var kept = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,k FROM p", cancellationToken: CancellationToken.None);
            int count = 0;
            await foreach (var row in kept.GetRowsAsync(CancellationToken.None))
            { row.GetValue(0).ShouldBe(sequence ? 1001L : 1000L); row.GetValue(1).ShouldBe(1000); count++; }
            count.ShouldBe(1);
            await using var next = (DatabaseResultSet)await session.ExecuteAsync("SELECT nextval('s')", cancellationToken: CancellationToken.None);
            await foreach (var row in next.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe(sequence ? 1002L : 1L); }
        }
        finally { SqlPlanExecutor.TableAsSourceAdmitted = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CTAS memory: grouped OFFSET sources refuse at 8 KiB (#1762)")]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ExecuteAsync_GroupedOffsetSmallGrant_ShouldRefuseSource(bool forceBase, bool sequence, bool temp)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("ctas-small-group");
        if (temp) { builder.Options.TempDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ctas-pins-" + Guid.NewGuid().ToString("N")); }
        builder.Options.StatementMemoryBudget = 8192;
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE SEQUENCE s", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(0, 1001).Select(k => $"({k})")), cancellationToken: CancellationToken.None);
        database.Definitions.ForceSelectRetentionBasePath = forceBase;
        string expression = sequence ? "nextval('s')" : "CAST(k AS BIGINT)";

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            $"CREATE TABLE p AS SELECT {expression} AS id,k FROM t GROUP BY k ORDER BY k LIMIT 1 OFFSET 1000", cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldContain(item => item.Code == "CS05067");
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        session.LastStatementMetrics.PeakHashMemoryBytes.ShouldBe(8160);
        session.LastStatementMetrics.RecordsExamined.ShouldBe(forceBase ? 86 : 37);
        database.Catalog.TryGetTable("dbo", "p", out _).ShouldBeFalse();
        _output.WriteLine($"CTAS_REFUSAL|base={forceBase}|{expression}|{session.LastStatementMetrics.PeakHashMemoryBytes}");
        await using var next = (DatabaseResultSet)await session.ExecuteAsync("SELECT nextval('s')", cancellationToken: CancellationToken.None);
        await foreach (var row in next.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe(1L); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: wide grouped DML preserves its original ledger (#1776)")]
    [InlineData("UPDATE a SET note=g.note FROM (SELECT note,COUNT(*) n FROM a GROUP BY note) g WHERE a.note=g.note", 6480)]
    [InlineData("DELETE FROM a USING (SELECT note,COUNT(*) n FROM a GROUP BY note) g WHERE a.note=g.note", 4912)]
    [InlineData("MERGE INTO a USING (SELECT note,COUNT(*) n FROM a GROUP BY note) g ON a.note=g.note WHEN MATCHED THEN UPDATE SET note=g.note", 8168)]
    public async Task ExecuteAsync_WideGroupedDml_ShouldKeepWorkOwner(string sql, long expectedPeak)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("wide-group-dml", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Setup(session);
        await session.ExecuteAsync("UPDATE a SET note='" + new string('x', 200) + "'", cancellationToken: CancellationToken.None);
        int reservations = 0;
        try
        {
            SqlStatementMetrics.AfterMemoryReservation = (metrics, _) =>
            {
                metrics.JoinedDmlStatement.ShouldNotBeNull();
                reservations++;
            };
            // Act
            var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
            // Assert
            result.AffectedCount.ShouldBe(2);
            reservations.ShouldBeGreaterThan(0);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
            session.LastStatementMetrics.PeakHashMemoryBytes.ShouldBe(expectedPeak);
            _output.WriteLine($"DML_GROUP|{sql}|{session.LastStatementMetrics.PeakHashMemoryBytes}");
        }
        finally { SqlStatementMetrics.AfterMemoryReservation = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: top N charges binary clones and credits null replacements (#1776)")]
    [InlineData(4407L, true)]
    [InlineData(4408L, false)]
    public async Task ExecuteAsync_BinaryHeapReplacement_ShouldChargeRealClones(long budget, bool refuses)
    {
        // Arrange: delivery clones shared binary cells. NULL replaces a binary candidate
        // before another binary replaces NULL, so every replacement uses its own size.
        var builder = SqlDatabaseEngine.CreateBuilder("binary-heap");
        builder.Options.StatementMemoryBudget = budget;
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE bins(k INT,p BINARY)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO bins VALUES(1,@p),(2,NULL),(3,@p)",
            new Dictionary<string, object?> { ["p"] = new byte[4000] }, CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE u(k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO u VALUES(1),(2)", cancellationToken: CancellationToken.None);

        // Act / Assert
        const string sql = "SELECT bins.p FROM bins CROSS JOIN u ORDER BY bins.k DESC LIMIT 1";
        if (refuses)
        {
            var failure = await Should.ThrowAsync<DatabaseException>(() => Read(session, sql));
            failure.Diagnostics.ShouldContain(item => item.Code == "CS05067");
            session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBe(192);
        }
        else
        {
            await Read(session, sql);
            session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBe(4408);
        }
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SELECT memory: a window does not adopt an already charged relation cache (#1776)")]
    public async Task ExecuteAsync_WindowOverCache_ShouldKeepOneCacheReservation()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-cache-owner", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES(1),(2),(3)", cancellationToken: CancellationToken.None);
        // Act
        await Read(session, "SELECT k,COUNT(*) OVER() FROM t");
        long ordinary = session.LastStatementMetrics!.PeakHashMemoryBytes;
        await Read(session, "WITH c AS (SELECT k FROM t) SELECT k,COUNT(*) OVER() FROM c");
        // Assert: the window has the same buffers, with one enclosing 288 B cache.
        ordinary.ShouldBe(4316);
        session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBe(4604);
        session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
        _output.WriteLine($"WINDOW_CACHE|{ordinary}|{session.LastStatementMetrics.PeakHashMemoryBytes}");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: one wide sort payload charges once across outputs and keys (#1776)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_SharedWideSort_ShouldPinRepeatedValueCharge(bool forceBase)
    {
        // Arrange: one 2 MiB string is shared by every output and a second sort key.
        var builder = SqlDatabaseEngine.CreateBuilder("shared-wide-charge");
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(0, 4096).Select(i => $"({i})")), cancellationToken: CancellationToken.None);
        database.Definitions.ForceSelectRetentionBasePath = forceBase;
        long before = GC.GetTotalMemory(forceFullCollection: true);
        string payload = new('x', 1048576);
        long retainedHeap = 0;
        try
        {
            SqlStatementMetrics.AfterMemoryReservation = (metrics, _) =>
            {
                if (metrics.HashMemoryBytes == 2654296)
                { retainedHeap = GC.GetTotalMemory(forceFullCollection: true) - before; }
            };
            // Act
            await using var rows = (DatabaseResultSet)await session.ExecuteAsync("SELECT @p AS p FROM t ORDER BY k,p",
                new Dictionary<string, object?> { ["p"] = payload }, CancellationToken.None);
            int count = 0;
            await foreach (var row in rows.GetRowsAsync(CancellationToken.None))
            { row.GetValue(0).ShouldBeSameAs(payload); count++; }
            // Assert: the collector owns one wide identity across outputs and keys.
            count.ShouldBe(4096);
            var metrics = session.LastStatementMetrics.ShouldNotBeNull();
            _output.WriteLine($"SHARED_RATIO|charged={metrics.PeakHashMemoryBytes}|retainedHeap={retainedHeap}|ratio={metrics.PeakHashMemoryBytes / (double)retainedHeap:F2}");
            metrics.PeakHashMemoryBytes.ShouldBe(2654296);
            metrics.HashMemoryBytes.ShouldBe(0);
            metrics.PlainSelectMemoryBytes.ShouldBe(0);
        }
        finally { SqlStatementMetrics.AfterMemoryReservation = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SELECT memory: counters and reservations preserve the ordinary DML ledger (#1776)")]
    public void ReserveHashMemory_ReferenceCounters_ShouldKeepOrdinaryWork()
    {
        // Arrange
        var metrics = new SqlStatementMetrics
        {
            SelfReferenceCandidateVisits = 1,
            SelfReferenceProbes = 1,
            ReferenceCatalogTableVisits = 1,
            IncomingReferenceBuilds = 1,
            ReferenceIntentLockBatches = 1,
            ConstraintLookupBuilds = 1,
            LiveIndexBuilds = 1,
        };
        // Act
        metrics.ReserveHashMemory(96, 96);
        // Assert
        metrics.DmlMemory(96).ShouldBeNull();
        metrics.HashMemoryBytes.ShouldBe(96);
        metrics.PeakHashMemoryBytes.ShouldBe(96);
        metrics.ReleaseHashMemory(96);
        metrics.HashMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: wire-sized projections pin their independent statement cost (#1776)")]
    [InlineData(false, false, 35656720L, 35656720L)]
    [InlineData(false, true, 35657112L, 35657112L)]
    [InlineData(true, false, 2103128L, 2103128L)]
    public async Task ExecuteAsync_WireSizedProjection_ShouldPinStatementCost(bool manyRows, bool explicitTransaction, long expectedDefaultPeak, long expectedPeak)
    {
        // Arrange: retain the wire tests' complete inputs and outputs; only their grant differs.
        // Wide identities share within each buffer, including constant-folded projections.
        long[] peaks = new long[2];
        for (int round = 0; round < 2; round++)
        {
            var builder = SqlDatabaseEngine.CreateBuilder("wire-sized-select");
            builder.Options.StatementMemoryBudget = (round == 0 ? 64L : 256L) * 1024 * 1024;
            await using var engine = await builder.BuildAsync(CancellationToken.None);
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("CREATE TABLE users(id INT NOT NULL,name VARCHAR(100),score BIGINT)", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync("INSERT INTO users(id,name,score) VALUES(1,'ada',100),(2,'grace',200)", cancellationToken: CancellationToken.None);
            if (explicitTransaction)
            {
                await session.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None);
                await session.ExecuteAsync("INSERT INTO users(id,name) VALUES(9,'pending')", cancellationToken: CancellationToken.None);
            }
            string columns = string.Join(", ", Enumerable.Range(1, 34).Select(ordinal =>
                $"CASE WHEN id=1 THEN 'partial' ELSE RPAD('x',524288,'x') END AS c{ordinal}"));
            string sql = manyRows
                ? "WITH c AS (SELECT 1 AS n UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 " +
                    "UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) " +
                    "SELECT @payload AS payload FROM c a CROSS JOIN c b LIMIT 65"
                : $"SELECT {columns} FROM users ORDER BY id";
            // Act
            await ReadOutput();
            peaks[round] = session.LastStatementMetrics!.PeakHashMemoryBytes;
            session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
            _output.WriteLine($"WIRE_COST|many={manyRows}|transaction={explicitTransaction}|round={round}|peak={peaks[round]}");

            async Task ReadOutput()
            {
                await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql,
                    manyRows ? new Dictionary<string, object?> { ["payload"] = new string('x', 1048576) } : null, CancellationToken.None);
                int count = 0;
                await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { _ = row.FieldCount; count++; }
                count.ShouldBe(manyRows ? 65 : explicitTransaction ? 3 : 2);
            }
        }
        // Assert
        peaks.ShouldBe([expectedDefaultPeak, expectedPeak]);
    }

    internal static async Task VerifyEveryUnwind(string sql, bool forceBase,
        Action<SqlDatabaseEngineBuilder>? configure = null, Func<SqlDatabaseSession, Task>? setup = null, Action<SqlDatabaseEngine>? verifyUnwind = null)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("every-buffer-unwind");
        builder.Options.StatementMemoryBudget = 1_048_576;
        configure?.Invoke(builder);
        builder.Functions.Add(SqlScalarFunction.Create("record_value", (long value) => value));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await (setup is null ? Setup(session) : setup(session));
        database.Definitions.ForceSelectRetentionBasePath = forceBase;
        int count = 0;
        try
        {
            SqlStatementMetrics.AfterMemoryReservation = (_, _) => count++;
            await Read(session, sql);
        }
        finally { SqlStatementMetrics.AfterMemoryReservation = null; }
        count.ShouldBeGreaterThan(0);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        for (int point = 1; point <= count; point++)
        {
            foreach (bool cancel in new[] { false, true })
            {
                using var cancellation = new CancellationTokenSource();
                int visited = 0;
                var fault = new InvalidOperationException("retained-buffer-fault");
                try
                {
                    SqlStatementMetrics.AfterMemoryReservation = (_, _) =>
                    {
                        if (++visited != point) { return; }
                        if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
                        throw fault;
                    };
                    if (cancel)
                    { await Should.ThrowAsync<OperationCanceledException>(() => Read(session, sql, cancellation.Token)); }
                    else
                    { (await Should.ThrowAsync<InvalidOperationException>(() => Read(session, sql))).ShouldBeSameAs(fault); }
                    visited.ShouldBeGreaterThanOrEqualTo(point);
                    verifyUnwind?.Invoke(engine);
                    session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
                    session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
                }
                finally { SqlStatementMetrics.AfterMemoryReservation = null; }
            }
        }
    }

    private static async Task Setup(SqlDatabaseSession session)
    {
        foreach (string sql in new[]
        {
            "CREATE SEQUENCE s",
            "CREATE TABLE spell(note TEXT COLLATE case_accent_insensitive,v INT)",
            "INSERT INTO spell VALUES('a',1),('Á́́́́́́́́́́́́́́́́',2)",
            "CREATE TABLE wide(k INT PRIMARY KEY,note TEXT)", "INSERT INTO wide VALUES(1,'" + new string('x', 2000) + "'),(2,'" + new string('y', 2000) + "')",
            "CREATE TABLE t(k INT,v INT)", "INSERT INTO t VALUES(1,1),(1,2),(2,3)",
            "CREATE VIEW view_t AS SELECT k FROM t",
            "CREATE TABLE u(k INT)", "INSERT INTO u VALUES(1),(2)",
            "CREATE TABLE a(id INT PRIMARY KEY,note TEXT)", "INSERT INTO a VALUES(1,'alpha'),(2,'beta')",
            "CREATE TABLE b(aid INT)", "INSERT INTO b VALUES(1),(2),(3)",
        }) { await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None); }
    }

    private static async Task Read(SqlDatabaseSession session, string sql, CancellationToken token = default)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: token);
        await foreach (var row in result.GetRowsAsync(token)) { _ = row.FieldCount; }
    }
}
