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

[Collection("SqlIdentityConcurrency")]
public sealed partial class SqlSequenceThirdPassTests
{
    private readonly ITestOutputHelper _output;

    public SqlSequenceThirdPassTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence retention: unbounded grouped and correlated queries match ordinary memory accounting")]
    [InlineData(0, false)][InlineData(1, false)][InlineData(2, false)]
    [InlineData(0, true)][InlineData(1, true)][InlineData(2, true)]
    public async Task Execute_UnboundedSequenceCorpus_ShouldMatchSequenceFreeBudget(int shape, bool ordered)
    {
        // Arrange: the old 296+ byte charge admits fewer than 28 of these 128 rows.
        const int count = 128;
        await using var engine = SqlDatabaseEngine.Create("third-pass", new() { StatementMemoryBudget = 8192 });
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");
        await Exec(session, "CREATE TABLE t(k INT)");
        await Exec(session, "CREATE TABLE u(k INT PRIMARY KEY)");
        await Exec(session, "CREATE TABLE dim(id BIGINT, k INT)");
        string values = string.Join(",", Enumerable.Range(1, count).Select(i => $"({i})"));
        await Exec(session, "INSERT INTO t VALUES " + values);
        await Exec(session, "INSERT INTO u VALUES " + values);
        string clauses = " FROM t" + (shape > 0 ? " WHERE EXISTS (SELECT 1 FROM u WHERE u.k=t.k)" : "") +
            (shape != 1 ? " GROUP BY t.k" : "") + (ordered ? " ORDER BY t.k" : "");
        var ordinary = await Rows(session, "SELECT t.k" + clauses);
        ordinary.Length.ShouldBe(count);
        session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBe(0);
        int retained = 0;
        SqlPlanExecutor.SequenceCandidatesRetained = value => retained = Math.Max(retained, value);
        try
        {
            // Act: include the reported INSERT SELECT form as well as correlated SELECT.
            object?[][] rows;
            if (shape == 0)
            {
                await Exec(session, "INSERT INTO dim(id,k) SELECT nextval('s'), t.k" + clauses);
                session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBe(0);
                rows = await Rows(session, "SELECT id,k FROM dim ORDER BY id");
            }
            else { rows = await Rows(session, "SELECT nextval('s'), t.k" + clauses); }

            // Assert: exact row/value counters and the retention hook, never elapsed time.
            rows.Length.ShouldBe(count);
            rows.Select(row => row[0]).ShouldBe(Enumerable.Range(1, count).Select(i => (object?)(long)i));
            rows.Select(row => row[1]).ShouldBe(ordinary.Select(row => row[0]));
            session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBe(0);
            session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
            retained.ShouldBe(ordered ? count : 0);
            (await Rows(session, "SELECT currval('s')"))[0][0].ShouldBe((long)count);
            _output.WriteLine($"shape={shape}; ordered={ordered}; ordinary={ordinary.Length}; sequence={rows.Length}; retained={retained}; peak grant=0; final grant=0; currval={count}");
        }
        finally
        {
            _output.WriteLine($"shape={shape}; ordered={ordered}; ordinary={ordinary.Length}; retained peak={retained}; grant after exit={session.LastStatementMetrics!.HashMemoryBytes}");
            SqlPlanExecutor.SequenceCandidatesRetained = null;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Unordered sequence projection: WHERE and HAVING precede projection for each surviving row")]
    [InlineData(" GROUP BY k HAVING k<>2 AND nextval('s')>0")]
    [InlineData(" WHERE k<>2 AND nextval('s')>0 AND EXISTS (SELECT 1 FROM u WHERE u.k=t.k)")]
    [InlineData(" WHERE EXISTS (SELECT 1 FROM u WHERE u.k=t.k) GROUP BY k HAVING k<>2 AND nextval('s')>0")]
    public async Task Execute_UnorderedFilteredSequenceCorpus_ShouldInterleavePredicateAndProjection(string clauses)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("third-pass", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");
        await Exec(session, "CREATE TABLE t(k INT)");
        await Exec(session, "CREATE TABLE u(k INT)");
        await Exec(session, "INSERT INTO t VALUES (1),(2),(3)");
        await Exec(session, "INSERT INTO u VALUES (1),(2),(3)");

        // Act
        var rows = await Rows(session, "SELECT nextval('s'), k FROM t" + clauses);

        // Assert: row 2 reaches neither sequence call; each predicate then projection consumes two.
        rows.Select(row => row[0]).ShouldBe(new object?[] { 2L, 4L });
        rows.Select(row => row[1]).ShouldBe(new object?[] { 1, 3 });
        (await Rows(session, "SELECT nextval('s')"))[0][0].ShouldBe(5L);
        _output.WriteLine("survivors=2; projected values=2,4; filtered row calls=0; next=5");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Unordered sequence window: project OFFSET rows before discarding and stop at LIMIT")]
    [InlineData(" GROUP BY k")]
    [InlineData(" WHERE EXISTS (SELECT 1 FROM u WHERE u.k=t.k)")]
    [InlineData(" WHERE EXISTS (SELECT 1 FROM u WHERE u.k=t.k) GROUP BY k")]
    public async Task Execute_UnorderedSequenceWindowCorpus_ShouldProjectOffsetRows(string clauses)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("third-pass", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");
        await Exec(session, "CREATE TABLE t(k INT)");
        await Exec(session, "CREATE TABLE u(k INT)");
        await Exec(session, "INSERT INTO t VALUES (1),(2),(3)");
        await Exec(session, "INSERT INTO u VALUES (1),(2),(3)");

        // Act
        var rows = await Rows(session, "SELECT nextval('s'), k FROM t" + clauses + " LIMIT 1 OFFSET 1");

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 2L, 2 });
        (await Rows(session, "SELECT nextval('s')"))[0][0].ShouldBe(3L);
        _output.WriteLine("offset=1; limit=1; projected calls=2; next=3");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Bounded sequence top N: charge and release on success, cancellation, predicate/projection errors and budget refusal")]
    [InlineData(false, 0)][InlineData(false, 1)][InlineData(false, 2)][InlineData(false, 3)][InlineData(false, 4)]
    [InlineData(true, 0)][InlineData(true, 1)][InlineData(true, 2)][InlineData(true, 3)][InlineData(true, 4)]
    public async Task Execute_BoundedSequenceTopNExit_ShouldReleaseEveryGrant(bool grouped, int exit)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("third-pass", new() { StatementMemoryBudget = exit == 4 ? 600 : 8192 });
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");
        await Exec(session, "CREATE TABLE t(k INT)");
        await Exec(session, "CREATE TABLE u(k INT)");
        await Exec(session, "INSERT INTO t VALUES (4),(3),(2),(1)");
        using var cancel = new CancellationTokenSource();
        int peak = 0;
        SqlPlanExecutor.SequenceCandidatesRetained = count =>
        {
            peak = Math.Max(peak, count);
            if (exit == 1 && count == 2) { cancel.Cancel(); }
        };
        string projection = exit == 3 ? "nextval('s') + 1/(k-1)" : "nextval('s')";
        string clauses = grouped ? " GROUP BY k" : " WHERE (EXISTS (SELECT 1 FROM u WHERE u.k=t.k) OR k>0)";
        if (exit == 2) { clauses += grouped ? " HAVING 1/(k-2)>0" : " AND 1/(k-2)>0"; }
        string sql = "SELECT " + projection + ", k FROM t" + clauses + " ORDER BY k LIMIT " + (exit == 4 ? "10" : "2");
        try
        {
            // Act
            if (exit == 0) { (await Rows(session, sql, cancel.Token)).Length.ShouldBe(2); }
            else if (exit == 1) { await Should.ThrowAsync<OperationCanceledException>(() => Rows(session, sql, cancel.Token)); }
            else
            {
                var failure = await Should.ThrowAsync<DatabaseException>(() => Rows(session, sql, cancel.Token));
                failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(exit == 4 ? "CS05067" : "CS02001");
            }

            // Assert
            var metrics = session.LastStatementMetrics!;
            if (grouped && exit == 2)
            {
                // HAVING completes before ordered deferral starts: no grant existed yet.
                peak.ShouldBe(0);
                metrics.PeakHashMemoryBytes.ShouldBe(0);
            }
            else
            {
                peak.ShouldBeGreaterThan(0);
                metrics.PeakHashMemoryBytes.ShouldBeGreaterThan(0);
            }
            metrics.HashMemoryBytes.ShouldBe(0);
            if (exit == 0) { peak.ShouldBe(2); }
            _output.WriteLine($"grouped={grouped}; exit={exit}; retained peak={peak}; charged peak={metrics.PeakHashMemoryBytes}; final grant={metrics.HashMemoryBytes}");
        }
        finally { SqlPlanExecutor.SequenceCandidatesRetained = null; }
    }

    private static async Task Exec(SqlDatabaseSession session, string sql)
    {
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        result.Diagnostics.ShouldBeEmpty();
    }

    private static async Task<object?[][]> Rows(SqlDatabaseSession session, string sql, CancellationToken token = default)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: token);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(token)) { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows.ToArray();
    }
}
