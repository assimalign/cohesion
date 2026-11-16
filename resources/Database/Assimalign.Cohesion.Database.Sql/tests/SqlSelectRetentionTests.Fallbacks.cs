using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSelectRetentionTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - SELECT retention: natural fallback conditions use original collectors (#1776)")]
    public async Task ExecuteAsync_FallbackConditions_ShouldUseBasePath()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("retention-fallbacks");
        builder.Functions.Add(SqlScalarFunction.Create("record_call", (long value) => value));
        builder.Functions.Add(SqlAggregateFunction.Create<long, long, long>("sum_calls", () => 0L,
            (state, value) => state + value, state => state));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(k INT)");
        await Execute(session, "CREATE TABLE u(k INT)");
        await Execute(session, "INSERT INTO t VALUES(1),(2),(2)");
        await Execute(session, "INSERT INTO u VALUES(1),(2)");
        var observed = new List<(string Name, bool Pipeline)>();
        database.Definitions.ObserveSelectRetention = (name, pipeline, _) => observed.Add((name, pipeline));
        // A real two-input body takes the explicit two-row argument through the join pipeline.
        (await Rows(session, "SELECT EXISTS(SELECT a.k FROM t a JOIN u b ON a.k=b.k)")).ShouldBe([[true]]);
        observed.ShouldContain(("subquery-body", true));
        observed.ShouldContain(("join-matches", true));
        foreach (var shape in new[]
        {
            // Recursive scratch, DML, window owner, correlated frame, own subquery, both call sides.
            ("group-input", "WITH RECURSIVE r(k) AS (SELECT 1 UNION ALL SELECT k+1 FROM r WHERE k<3) SELECT COUNT(*) FROM r"),
            ("group-input", "INSERT INTO u SELECT COUNT(*) FROM t"),
            ("group-input", "SELECT COUNT(*) OVER() FROM (SELECT k,COUNT(*) FROM t GROUP BY k) d"),
            ("group-input", "SELECT k,COUNT(*) FROM t GROUP BY k HAVING EXISTS(SELECT 1 FROM u WHERE u.k=t.k)"),
            ("group-input", "SELECT k,COUNT(*) FROM t GROUP BY k HAVING EXISTS(SELECT 1 FROM u)"),
            ("group-input", "SELECT SUM(record_call(k)) FROM t WHERE record_call(k)>0"),
            ("group-input", "SELECT SUM(record_call(k)) FROM t"),
            ("group-input", "SELECT sum_calls(k) FROM t"),
            ("group-input", "SELECT SUM(JSON_VALUE('{\"k\":1}', '$.k' RETURNING INT)) FROM t"),
            ("join-matches", "SELECT record_call(a.k) FROM t a JOIN u b ON a.k=b.k WHERE record_call(b.k)>0"),
            ("join-matches", "SELECT record_call(a.k) FROM t a JOIN u b ON a.k=b.k"),
            ("join-matches", "SELECT a.k,(SELECT MIN(k) FROM u) FROM t a CROSS JOIN u b"),
            ("join-matches", "SELECT a.k FROM t a RIGHT JOIN u b ON record_call(a.k)=b.k WHERE record_call(b.k)>0"),
            ("join-matches", "SELECT a.k FROM t a RIGHT JOIN u b ON a.k=b.k WHERE record_call(b.k)>0"),
            ("group-input", "SELECT COUNT(*) FROM t a RIGHT JOIN u b ON a.k=b.k WHERE record_call(b.k)>0"),
            ("distinct", "SELECT DISTINCT k FROM t ORDER BY k"),
            ("distinct", "SELECT DISTINCT k FROM t GROUP BY k"),
            ("distinct", "SELECT DISTINCT k,EXISTS(SELECT 1 FROM u WHERE u.k=t.k) FROM t"),
            // IN supplies a deferred collation even though its independent set is already cached.
            ("distinct", "SELECT DISTINCT k IN (SELECT k FROM u) FROM t"),
            ("subquery-body", "SELECT EXISTS(SELECT DISTINCT k FROM t)"),
            ("subquery-body", "SELECT EXISTS(SELECT k FROM t ORDER BY k)"),
        })
        {
            // Act
            observed.Clear();
            var result = await session.ExecuteAsync(shape.Item2, cancellationToken: CancellationToken.None);
            if (result is IAsyncDisposable disposable) { await disposable.DisposeAsync(); }

            // Assert: force-base is unset; these are real eligibility refusals.
            observed.ShouldContain(item => item.Name == shape.Item1 && !item.Pipeline, shape.Item2);
            observed.ShouldNotContain(item => item.Name == shape.Item1 && item.Pipeline, shape.Item2);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT retention: cancellation during a pipelined hash probe releases all builds (#1776)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CancelledPipeline_ShouldReleaseHashBuild(bool grouping)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("retention-cancel");
        builder.Functions.Add(SqlScalarFunction.Create("cancel_row", (long value) =>
        {
            if (++calls == 7) { cancellation.Cancel(); }
            return value;
        }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "t", "u" })
        {
            await Execute(session, $"CREATE TABLE {table}(k INT)");
            await Execute(session, $"INSERT INTO {table} VALUES" + string.Join(",", Enumerable.Range(1, 40).Select(i => $"({i})")));
        }
        var sql = grouping ? "SELECT SUM(t.k) FROM t JOIN u ON t.k=u.k WHERE cancel_row(t.k)>0"
            : "SELECT t.k FROM t JOIN u ON t.k=u.k WHERE cancel_row(t.k)>0 LIMIT 1";

        // Act
        var error = await Should.ThrowAsync<OperationCanceledException>(async () =>
            await session.ExecuteAsync(sql, cancellationToken: cancellation.Token));

        // Assert
        calls.ShouldBe(7);
        error.CancellationToken.ShouldBe(cancellation.Token);
        session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBeGreaterThan(0);
        session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
        (await Rows(session, "SELECT COUNT(*) FROM t")).ShouldBe([[40L]]);
    }
}
