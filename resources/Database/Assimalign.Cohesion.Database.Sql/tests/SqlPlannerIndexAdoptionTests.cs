using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Planner index adoption (#913): sargable predicates on indexed columns produce
/// seek plans (equality prefixes, range bounds where the encoding order supports
/// them), everything else falls back to the per-object scan, seek execution is
/// snapshot-equivalent to the scan it replaces, and seeks demonstrably examine
/// O(matches) records where a scan examines O(table).
/// </summary>
public sealed class SqlPlannerIndexAdoptionTests
{
    private static async Task<(SqlDatabaseEngine Engine, IDatabase Database, IDatabaseSession Session)> CreateAsync(string name)
    {
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = name });
        var database = await engine.CreateDatabaseAsync(name + "-db");
        var session = await database.CreateSessionAsync();
        return (engine, database, session);
    }

    private static SqlPlan PlanOf(IDatabase database, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var instance = (SqlDatabaseInstance)database;
        var request = parameters is null ? SqlQueryRequest.FromSql(sql) : SqlQueryRequest.FromSql(sql, parameters);
        return new SqlPlanner(instance.Catalog, request.Parameters).Plan(request.Statement.SqlExpression);
    }

    private static async Task<List<object?[]>> Rows(IDatabaseSession session, string sql)
    {
        var result = await session.ExecuteAsync(sql);
        var resultSet = result.ShouldBeAssignableTo<QueryResultSet>();

        var rows = new List<object?[]>();
        await foreach (var row in resultSet!.GetRowsAsync())
        {
            var values = new object?[row.FieldCount];
            for (int i = 0; i < row.FieldCount; i++)
            {
                values[i] = row.GetValue(i);
            }
            rows.Add(values);
        }

        return rows;
    }

    private static SqlStatementMetrics MetricsOf(IDatabaseSession session)
        => ((SqlDatabaseSession)session).LastStatementMetrics.ShouldNotBeNull();

    // ── Plan shape ─────────────────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Planner: sargable predicates on indexed columns produce seek plans, everything else scans")]
    public async Task Plan_AccessPathSelection_ShouldFollowSargabilityRules()
    {
        // Arrange: single-column, composite, and string indexes.
        var (engine, database, session) = await CreateAsync("plan-shape");
        await using var engineLifetime = engine;
        await using var sessionLifetime = session;
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, tenant INT, seq INT, name VARCHAR(100), note VARCHAR(100))");
        await session.ExecuteAsync("CREATE INDEX ix_id ON t (id)");
        await session.ExecuteAsync("CREATE INDEX ix_tenant_seq ON t (tenant, seq)");
        await session.ExecuteAsync("CREATE INDEX ix_name ON t (name)");

        static SqlIndexSeekPath Seek(SqlPlan plan) =>
            ((SqlSelectPlan)plan).Access.ShouldBeOfType<SqlIndexSeekPath>();

        static void Scan(SqlPlan plan) =>
            ((SqlSelectPlan)plan).Access.ShouldBeOfType<SqlScanPath>();

        // Indexed equality → seek on that index.
        Seek(PlanOf(database, "SELECT id FROM t WHERE id = 5")).Index.Name.ShouldBe("ix_id");

        // Parameters are plan-time comparands too.
        Seek(PlanOf(database, "SELECT id FROM t WHERE id = @p", new Dictionary<string, object?> { ["p"] = 7 })).Index.Name.ShouldBe("ix_id");

        // Composite prefix rules: leading column alone seeks; a longer equality
        // prefix wins over the single-column candidate's tie; a trailing column
        // alone cannot seek.
        Seek(PlanOf(database, "SELECT id FROM t WHERE tenant = 1")).Index.Name.ShouldBe("ix_tenant_seq");
        var composite = Seek(PlanOf(database, "SELECT id FROM t WHERE tenant = 1 AND seq = 2"));
        composite.Index.Name.ShouldBe("ix_tenant_seq");
        composite.EqualityValues.Count.ShouldBe(2);
        Scan(PlanOf(database, "SELECT id FROM t WHERE seq = 2"));

        // Equality prefix + range on the next key column.
        var prefixRange = Seek(PlanOf(database, "SELECT id FROM t WHERE tenant = 1 AND seq > 10"));
        prefixRange.EqualityValues.Count.ShouldBe(1);
        prefixRange.Lower.ShouldNotBeNull();

        // Ranges on order-safe types seek; BETWEEN is its two bounds.
        var range = Seek(PlanOf(database, "SELECT id FROM t WHERE id BETWEEN 10 AND 20"));
        range.Lower!.Value.Inclusive.ShouldBeTrue();
        range.Upper!.Value.Inclusive.ShouldBeTrue();

        // Strings: equality seeks, ranges do not (Collation.Binary code-point
        // order diverges from ordinal comparison for astral planes).
        Seek(PlanOf(database, "SELECT id FROM t WHERE name = 'ada'")).Index.Name.ShouldBe("ix_name");
        Scan(PlanOf(database, "SELECT id FROM t WHERE name > 'ada'"));

        // Non-sargable shapes fall back: non-indexed column, column-to-column
        // comparison, computed column, null comparand, OR at the top level.
        Scan(PlanOf(database, "SELECT id FROM t WHERE note = 'x'"));
        Scan(PlanOf(database, "SELECT id FROM t WHERE id = seq"));
        Scan(PlanOf(database, "SELECT id FROM t WHERE ABS(id) = 5"));
        Scan(PlanOf(database, "SELECT id FROM t WHERE id = NULL"));
        Scan(PlanOf(database, "SELECT id FROM t WHERE id = 5 OR seq = 2"));

        // A conjunct on the index plus residual conjuncts still seeks — the
        // residual predicate keeps it correct.
        Seek(PlanOf(database, "SELECT id FROM t WHERE id = 5 AND note = 'x'")).Index.Name.ShouldBe("ix_id");
    }

    // ── Correctness equivalence ────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Planner: seek results equal scan results under concurrent committed and uncommitted writers")]
    public async Task Seek_UnderConcurrentWriters_ShouldEqualScanResults()
    {
        // Arrange: two tables with identical data — one indexed (seeks), one not
        // (scans) — and identical concurrent modifications, so any visibility
        // divergence between the paths shows up as a result difference.
        var (engine, database, session) = await CreateAsync("equivalence");
        await using var engineLifetime = engine;
        await using var sessionLifetime = session;
        await session.ExecuteAsync("CREATE TABLE indexed (id INT NOT NULL, v INT)");
        await session.ExecuteAsync("CREATE TABLE plain (id INT NOT NULL, v INT)");
        await session.ExecuteAsync("CREATE INDEX ix_id ON indexed (id)");

        foreach (string table in new[] { "indexed", "plain" })
        {
            var values = string.Join(", ", Enumerable.Range(0, 50).Select(i => $"({i}, {i * 10})"));
            await session.ExecuteAsync($"INSERT INTO {table} (id, v) VALUES {values}");

            // Committed history: one update, one delete.
            await session.ExecuteAsync($"UPDATE {table} SET v = 999 WHERE id = 7");
            await session.ExecuteAsync($"DELETE FROM {table} WHERE id = 9");
        }

        // An uncommitted writer mutates both tables identically.
        await using var writerSession = await database.CreateSessionAsync();
        var uncommitted = await writerSession.BeginTransactionAsync();
        foreach (string table in new[] { "indexed", "plain" })
        {
            await writerSession.ExecuteAsync($"INSERT INTO {table} (id, v) VALUES (7, -1)");
            await writerSession.ExecuteAsync($"DELETE FROM {table} WHERE id = 8");
        }

        // Act + Assert: point, range, and absent-key queries agree between the
        // seek path and the scan path, and the paths really differ.
        foreach (string predicate in new[] { "id = 7", "id BETWEEN 5 AND 12", "id = 9", "id = 8" })
        {
            var viaSeek = await Rows(session, $"SELECT id, v FROM indexed WHERE {predicate} ORDER BY id");
            MetricsOf(session).AccessPath.ShouldBe("seek:ix_id");

            var viaScan = await Rows(session, $"SELECT id, v FROM plain WHERE {predicate} ORDER BY id");
            MetricsOf(session).AccessPath.ShouldBe("scan");

            viaSeek.Select(row => ((int)row[0]!, (int)row[1]!))
                .ShouldBe(viaScan.Select(row => ((int)row[0]!, (int)row[1]!)));
        }

        await uncommitted.RollbackAsync();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Planner: a seek never surfaces a version the equivalent scan would hide")]
    public async Task Seek_SnapshotVisibility_ShouldMatchScanSemantics()
    {
        // Arrange
        var (engine, database, session) = await CreateAsync("seek-vis");
        await using var engineLifetime = engine;
        await using var sessionLifetime = session;
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, v INT)");
        await session.ExecuteAsync("CREATE INDEX ix_id ON t (id)");
        await session.ExecuteAsync("INSERT INTO t (id, v) VALUES (1, 10)");

        // A reader pins a snapshot, then a writer deletes the row and updates
        // another session's view of the world.
        await using var readerSession = await database.CreateSessionAsync();
        var pinned = await readerSession.BeginTransactionAsync(IsolationLevel.Snapshot);
        (await Rows(readerSession, "SELECT v FROM t WHERE id = 1")).Count.ShouldBe(1);
        MetricsOf(readerSession).AccessPath.ShouldBe("seek:ix_id");

        await session.ExecuteAsync("DELETE FROM t WHERE id = 1");

        // Assert: the pinned snapshot still sees the row THROUGH THE SEEK; a
        // fresh snapshot does not.
        (await Rows(readerSession, "SELECT v FROM t WHERE id = 1")).Count.ShouldBe(1);
        MetricsOf(readerSession).AccessPath.ShouldBe("seek:ix_id");
        await pinned.RollbackAsync();

        (await Rows(session, "SELECT v FROM t WHERE id = 1")).ShouldBeEmpty();
        MetricsOf(session).AccessPath.ShouldBe("seek:ix_id");
    }

    // ── Range semantics ────────────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Planner: range seeks honor bound inclusivity exactly")]
    public async Task Seek_RangeBounds_ShouldHonorInclusivity()
    {
        // Arrange
        var (engine, database, session) = await CreateAsync("seek-range");
        await using var engineLifetime = engine;
        await using var sessionLifetime = session;
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");
        await session.ExecuteAsync("CREATE INDEX ix_id ON t (id)");
        var values = string.Join(", ", Enumerable.Range(0, 100).Select(i => $"({i})"));
        await session.ExecuteAsync($"INSERT INTO t (id) VALUES {values}");

        static int[] Ids(List<object?[]> rows) => rows.Select(row => (int)row[0]!).ToArray();

        // Act + Assert
        Ids(await Rows(session, "SELECT id FROM t WHERE id BETWEEN 10 AND 20 ORDER BY id"))
            .ShouldBe(Enumerable.Range(10, 11).ToArray());
        MetricsOf(session).AccessPath.ShouldBe("seek:ix_id");
        MetricsOf(session).RecordsExamined.ShouldBe(11);

        Ids(await Rows(session, "SELECT id FROM t WHERE id > 10 AND id < 20 ORDER BY id"))
            .ShouldBe(Enumerable.Range(11, 9).ToArray());
        MetricsOf(session).RecordsExamined.ShouldBe(9);

        Ids(await Rows(session, "SELECT id FROM t WHERE id >= 95 ORDER BY id"))
            .ShouldBe(Enumerable.Range(95, 5).ToArray());

        Ids(await Rows(session, "SELECT id FROM t WHERE id < 3 ORDER BY id"))
            .ShouldBe(new[] { 0, 1, 2 });
    }

    // ── The sub-linear proof ───────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Planner: an indexed equality seek examines O(matches) records; the scan examines O(table)")]
    public async Task Seek_RecordsExamined_ShouldBeSubLinear()
    {
        // Arrange: enough rows that the gap is unambiguous.
        var (engine, database, session) = await CreateAsync("seek-cost");
        await using var engineLifetime = engine;
        await using var sessionLifetime = session;
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, note VARCHAR(50))");
        await session.ExecuteAsync("CREATE INDEX ix_id ON t (id)");

        const int rowCount = 2000;
        for (int start = 0; start < rowCount; start += 100)
        {
            var values = string.Join(", ", Enumerable.Range(start, 100).Select(i => $"({i}, 'n{i}')"));
            await session.ExecuteAsync($"INSERT INTO t (id, note) VALUES {values}");
        }

        // Act: the same logical lookup through the index and past it.
        (await Rows(session, "SELECT note FROM t WHERE id = 1500")).Count.ShouldBe(1);
        var seekMetrics = MetricsOf(session);
        seekMetrics.AccessPath.ShouldBe("seek:ix_id");
        long seekExamined = seekMetrics.RecordsExamined;

        (await Rows(session, "SELECT id FROM t WHERE note = 'n1500'")).Count.ShouldBe(1);
        var scanMetrics = MetricsOf(session);
        scanMetrics.AccessPath.ShouldBe("scan");
        long scanExamined = scanMetrics.RecordsExamined;

        // Assert: the seek touched exactly its match; the scan decoded the table.
        seekExamined.ShouldBe(1);
        scanExamined.ShouldBeGreaterThanOrEqualTo(rowCount);

        // COUNT(*) rides the same access path.
        (await Rows(session, "SELECT COUNT(*) FROM t WHERE id = 1500")).Single()[0].ShouldBe(1L);
        MetricsOf(session).AccessPath.ShouldBe("seek:ix_id");
        MetricsOf(session).RecordsExamined.ShouldBe(1);
    }
}
