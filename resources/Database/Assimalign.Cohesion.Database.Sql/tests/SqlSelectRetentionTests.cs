using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed partial class SqlSelectRetentionTests
{
    private readonly ITestOutputHelper _output;

    public SqlSelectRetentionTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT retention: generated corpus matches the materialized path, including calls and analysis (#1776)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecuteAsync_GeneratedRetentionCorpus_ShouldMatchBase(int transport)
    {
        // Arrange: seed controls insertion order, duplicate spelling, NULLs and stable sort ties.
        var calls = new List<long>();
        var builder = SqlDatabaseEngine.CreateBuilder("retention-corpus");
        builder.Functions.Add(SqlScalarFunction.Create("record_call", (long value) => { calls.Add(value); return value; }));
        builder.Functions.Add(SqlAggregateFunction.Create<long, long, long>("sum_calls", () => 0L,
            (state, value) => { calls.Add(1000 + value); return state + value; }, state => state));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using IDuplexConnectionListener? listener = transport == 0 ? null : transport == 1
            ? new InMemoryConnectionListener()
            : TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await using var server = listener is null ? null : SqlDatabaseServer.Create(engine, new() { Listener = listener });
        if (server is not null) { await server.StartAsync(CancellationToken.None); }
        await using var client = listener is null ? null : SqlClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "db", Principal = "retention", EndPoint = listener.EndPoint },
            ConnectionFactory = transport == 1 ? ((InMemoryConnectionListener)listener).CreateFactory() : new TcpConnectionFactory(),
        });
        await using var connection = client is null ? null : await client.ConnectAsync(CancellationToken.None);
        var executionSession = connection is null ? session : (SqlDatabaseSession)server!.Sessions.Single().DatabaseSession!;
        var parameters = new Dictionary<string, object?> { ["take"] = 2, ["skip"] = 1, ["key"] = 2 };
        await Execute("CREATE SEQUENCE s");
        await Execute("CREATE TABLE t(id INT PRIMARY KEY,k INT,c CHAR(6),ci TEXT COLLATE case_insensitive,doc TEXT,b BINARY,j JSONB)");
        await Execute("CREATE TABLE u(id INT PRIMARY KEY,k INT)");
        await Execute("INSERT INTO u VALUES(1,1),(2,2),(4,4)");
        await Execute("CREATE VIEW v AS SELECT id,k,c,ci,doc,b,j FROM t");
        var observed = new List<(string Operator, bool Pipeline, int Rows)>();
        var mismatches = new List<string>();
        database.Definitions.ObserveSelectRetention = (name, pipeline, count) => observed.Add((name, pipeline, count));
        int statements = 0;
        for (int seed = 0; seed < 3; seed++)
        {
            await Execute("DELETE FROM t");
            foreach (int id in Enumerable.Range(1, 9).OrderBy(id => (id * 7 + seed * 3) % 11))
            {
                int key = id % 3;
                string text = new((char)('a' + key), 113 + seed * 44);
                await Execute($"INSERT INTO t VALUES({id},{(key == 0 ? "NULL" : key)},'{(key == 0 ? "a " : "a")}'," +
                    $"'{(id % 2 == 0 ? "Ab" : "aB")}','{text}',@bytes,CAST('{{\"k\":{key}}}' AS JSONB))",
                    new Dictionary<string, object?> { ["bytes"] = new byte[] { (byte)(id % 3), (byte)id } });
            }
            foreach (var row in Corpus())
            {
                // Act / Assert: each executed statement proves its own operator choices.
                var baseline = await Capture(row, true);
                var pipelined = await Capture(row, false);
                Check(() => pipelined.ShouldBe(baseline, $"seed={seed}; transport={transport}; {row.Sql}"));
                // EXPLAIN ANALYZE refuses CTAS before planning; only executable forms belong here.
                if (!row.TableAs)
                {
                    var analyzed = await Capture(row, false, "EXPLAIN ANALYZE ");
                    var baseAnalyzed = await Capture(row, true, "EXPLAIN ANALYZE ");
                    Check(() => analyzed.ShouldBe(baseAnalyzed, row.Sql));
                }
                var explained = await Capture(row, false, "EXPLAIN ");
                var baseExplained = await Capture(row, true, "EXPLAIN ");
                Check(() => explained.ShouldBe(baseExplained, row.Sql));
                statements += row.TableAs ? 4 : 6;
            }
        }
        _output.WriteLine($"transport={transport}; differential statements={statements}");
        mismatches.ShouldBeEmpty(string.Join(Environment.NewLine, mismatches));

        void Check(Action assertion)
        {
            try { assertion(); }
            catch (ShouldAssertException failure) { mismatches.Add(failure.Message); }
        }

        async Task Execute(string sql, IReadOnlyDictionary<string, object?>? values = null)
        {
            if (connection is not null) { await connection.ExecuteAsync(sql, values, CancellationToken.None); }
            else { await session.ExecuteAsync(sql, values, CancellationToken.None); }
        }

        async Task<string[]> Read(string sql)
        {
            var result = new List<string>();
            if (connection is not null)
            {
                var rows = await connection.QueryAsync(sql, parameters, CancellationToken.None);
                result.Add("affected=" + rows.AffectedCount);
                result.AddRange(rows.Columns.Select(column => $"{column.Name}:{column.Type}:{column.MaxLength}"));
                foreach (var row in rows)
                { result.Add(string.Join("|", Enumerable.Range(0, rows.Columns.Count).Select(i => Cell(row.GetValue(i))))); }
            }
            else
            {
                var executed = await session.ExecuteAsync(sql, parameters, CancellationToken.None);
                result.Add("affected=" + executed.AffectedCount);
                if (executed is not DatabaseResultSet rows) { return result.ToArray(); }
                await using var disposal = rows;
                result.AddRange(rows.Columns.Select(column => $"{column.Name}:{column.Type}:{column.MaxLength}:{column.IsNullable}"));
                await foreach (var row in rows.GetRowsAsync(CancellationToken.None))
                { result.Add(string.Join("|", Enumerable.Range(0, row.FieldCount).Select(i => Cell(row.GetValue(i))))); }
            }
            return result.ToArray();
        }

        async Task<string[]> Capture(RetentionCase row, bool forceBase, string prefix = "")
        {
            database.Definitions.ForceSelectRetentionBasePath = forceBase;
            if (row.TableAs) { await Execute("DROP TABLE IF EXISTS d"); }
            await Execute("ALTER SEQUENCE s RESTART WITH 1");
            calls.Clear();
            int observationStart = observed.Count;
            string sql = prefix + row.Sql;
            var captured = new List<string>();
            string? code = null;
            try { captured.AddRange(await Read(sql)); }
            catch (Exception failure) when (failure is DatabaseException or SqlClientException)
            {
                var diagnostics = failure is DatabaseException databaseFailure ? databaseFailure.Diagnostics : ((SqlClientException)failure).Diagnostics;
                code = diagnostics.First(item => item.Severity == DiagnosticSeverity.Error).Code;
                captured.Add(row.MultipleFailures ? "failure" : "failure=" + code);
            }
            if (prefix.Length == 0)
            {
                Check(() => (code is not null).ShouldBe(row.Fails, sql + "; diagnostic=" + code));
                if (row.FailureCode is not null) { Check(() => code.ShouldBe(row.FailureCode, sql)); }
            }
            var delta = observed.Skip(observationStart).ToArray();
            if (forceBase || prefix == "EXPLAIN ")
            { Check(() => delta.ShouldNotContain(item => item.Pipeline, sql)); }
            else
            {
                foreach (string name in row.Pipelines)
                { Check(() => delta.ShouldContain(item => item.Operator == name && item.Pipeline, sql)); }
                Check(() => delta.ShouldNotContain(item => item.Pipeline && !row.Pipelines.Contains(item.Operator), sql));
                if (code is null)
                {
                    foreach (string name in row.BaseOperators)
                    { Check(() => delta.ShouldContain(item => item.Operator == name && !item.Pipeline, sql)); }
                }
            }
            var metrics = executionSession.LastStatementMetrics!;
            Check(() => metrics.HashMemoryBytes.ShouldBe(0, sql));
            captured.Add($"work={metrics.RecordsExamined}:{metrics.JoinCandidatePairs}:{metrics.AccessPath}:{metrics.PeakOrderedRows}:" +
                $"{metrics.HashMemoryBytes}:" + string.Join(",", metrics.JoinAccessPaths ?? []));
            captured.Add("calls=" + string.Join(",", calls));
            if (row.TableAs && code is null && prefix != "EXPLAIN ")
            { captured.AddRange(await Read("SELECT * FROM d")); }
            captured.AddRange(await Read("SELECT nextval('s')"));
            return captured.ToArray();
        }
    }

    private sealed record RetentionCase(string Sql, string[] Pipelines, bool Fails = false,
        bool MultipleFailures = false, string? FailureCode = null, bool TableAs = false)
    {
        internal string[] BaseOperators { get; init; } = [];
    }

    private static RetentionCase Pipeline(string sql, params string[] operators) => new(sql, operators);
    private static RetentionCase Fallback(string sql, params string[] operators) => new(sql, []) { BaseOperators = operators };

    private static IEnumerable<RetentionCase> Corpus()
    {
        foreach (string key in new[] { "k", "c", "ci", "doc", "b", "j" })
        {
            yield return Pipeline($"SELECT {key},COUNT(*),MIN(b),MAX(b) FROM t GROUP BY {key}", "group-input");
            yield return Pipeline($"SELECT {key},SUM(id) FROM t GROUP BY {key} HAVING COUNT(*)>0 ORDER BY SUM(id) LIMIT @take OFFSET @skip", "group-input");
            foreach (string page in new[] { "", " LIMIT 1", " LIMIT 0", " LIMIT @take OFFSET @skip" })
            { yield return Pipeline($"SELECT DISTINCT {key} FROM t" + page, "distinct"); }
        }
        foreach (string join in new[] { "INNER", "LEFT", "RIGHT", "FULL" })
        {
            foreach (string page in new[] { "", " LIMIT 2 OFFSET 1", " LIMIT 0", " ORDER BY t.k LIMIT 2" })
            { yield return Pipeline($"SELECT t.c,t.b,u.id FROM t {join} JOIN u ON t.id=u.id" + page, "join-matches"); }
            yield return Pipeline($"SELECT t.c,COUNT(*) FROM t {join} JOIN u ON t.k=u.k GROUP BY t.c", "group-input");
        }
        yield return Pipeline("SELECT COUNT(*) FROM t WHERE id<0", "group-input");
        yield return Pipeline("SELECT COUNT(*)", "group-input");
        yield return Pipeline("SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES", "group-input");
        yield return Pipeline("SELECT COUNT(*) FROM t WHERE id=@key", "group-input");
        yield return Pipeline("SELECT COUNT(*) FROM t WHERE c LIKE 'a%'", "group-input");
        yield return Fallback("SELECT SUM(JSON_VALUE('{\"k\":1}', '$.k' RETURNING INT)) FROM t", "group-input");
        yield return Fallback("SELECT JSON_VALUE('{\"k\":1}', '$.k' RETURNING INT) FROM t a JOIN u b ON a.k=b.k LIMIT 2", "join-matches");
        yield return Pipeline("SELECT k,COUNT(*) FROM v GROUP BY k", "group-input");
        yield return Pipeline("SELECT k,COUNT(*) FROM (SELECT k FROM t ORDER BY id) d GROUP BY k", "group-input");
        yield return Pipeline("SELECT a.id,b.id,c.id,d.id FROM t a FULL JOIN u b ON a.k=b.k JOIN t c ON c.id=b.id LEFT JOIN u d ON c.k=d.k LIMIT 2", "join-matches");
        yield return Pipeline("SELECT a.id,b.id FROM t a JOIN u b ON a.k>b.k LIMIT 2", "join-matches");
        yield return Fallback("SELECT a.id,nextval('s') FROM t a JOIN u b ON a.k=b.k LIMIT 2", "join-matches");
        yield return Fallback("SELECT a.id,nextval('s') FROM t a JOIN u b ON a.k=b.k ORDER BY a.id LIMIT 2 OFFSET 1", "join-matches");
        yield return Fallback("SELECT record_call(a.id) FROM t a JOIN u b ON a.k=b.k ORDER BY a.id LIMIT 2", "join-matches");
        yield return Fallback("SELECT record_call(a.id) FROM t a JOIN u b ON a.k=b.k WHERE record_call(b.id)>0 LIMIT 2", "join-matches");
        yield return Fallback("SELECT SUM(record_call(id)) FROM t WHERE record_call(id)>0", "group-input");
        yield return Fallback("SELECT sum_calls(id) FROM t WHERE record_call(id)>0", "group-input");
        yield return Pipeline("SELECT SUM(10/(id-2)) FROM t WHERE record_call(id)>0", "group-input") with { Fails = true };
        yield return Fallback("SELECT SUM(record_call(id)) FROM t WHERE 10/(id-2)<>0", "group-input") with { Fails = true };
        yield return Fallback("SELECT record_call(a.id) FROM t a JOIN u b ON a.k=b.k WHERE 10/(a.id-2)<>0", "join-matches") with { Fails = true };
        yield return Pipeline("SELECT 10/(a.id-2) FROM t a JOIN u b ON a.k=b.k WHERE record_call(a.id)>0", "join-matches") with { Fails = true };
        yield return Pipeline("SELECT a.id FROM t a JOIN u b ON a.k=b.k WHERE 10/(a.id-2)<>0 ORDER BY a.id LIMIT 2", "join-matches") with { Fails = true };
        yield return Pipeline("SELECT 10/(a.id-2) FROM t a JOIN u b ON a.k=b.k WHERE record_call(a.id)>0 ORDER BY a.id LIMIT 2", "join-matches") with { Fails = true };
        yield return Pipeline("SELECT 10/(a.id-2) FROM t a JOIN u b ON a.k=b.k WHERE 10/(a.id-5)<>0 ORDER BY a.id LIMIT 2", "join-matches") with { Fails = true, MultipleFailures = true };
        yield return Pipeline("SELECT a.id FROM t a RIGHT JOIN u b ON record_call(a.id)=b.id WHERE 10/(b.id-2)<>0 ORDER BY a.id LIMIT 2", "join-matches") with { Fails = true };
        yield return Pipeline("SELECT COUNT(*) FROM t a RIGHT JOIN u b ON record_call(a.id)=b.id WHERE 10/(b.id-2)<>0", "group-input") with { Fails = true };
        yield return Fallback("SELECT a.id FROM t a FULL JOIN u b ON 10/(a.id-2)=b.id WHERE record_call(b.id)>0", "join-matches") with { Fails = true };
        yield return Fallback("SELECT COUNT(*) FROM t a FULL JOIN u b ON 10/(a.id-2)=b.id WHERE record_call(b.id)>0", "group-input") with { Fails = true };
        yield return Pipeline("SELECT a.id FROM t a JOIN u b ON a.k=b.k WHERE nextval('s')>0 LIMIT 1", "join-matches");
        yield return Pipeline("SELECT COUNT(*) FROM t WHERE nextval('s')>0", "group-input");
        yield return Pipeline("SELECT DISTINCT NEXT VALUE FOR s FROM t", "distinct");
        yield return Fallback("SELECT a.id,NEXT VALUE FOR s FROM t a JOIN u b ON a.k=b.k", "join-matches");
        // The reference also refuses NEXT VALUE FOR beneath EXISTS; nextval is executable there.
        yield return Fallback("SELECT EXISTS(SELECT NEXT VALUE FOR s FROM t)") with { Fails = true, FailureCode = "CS03076" };
        yield return Pipeline("SELECT EXISTS(SELECT nextval('s') FROM t)", "subquery-body");
        yield return Fallback("SELECT COUNT(*) FROM t WHERE NEXT VALUE FOR s>0") with { Fails = true, FailureCode = "CS03076" };
        yield return Pipeline("SELECT DISTINCT k FROM t WHERE nextval('s')>0 LIMIT 1", "distinct");
        yield return Pipeline("SELECT EXISTS(SELECT a.id FROM t a JOIN u b ON a.k=b.k WHERE nextval('s')>0)", "subquery-body", "join-matches");
        yield return Pipeline("SELECT DISTINCT nextval('s') FROM t LIMIT 1", "distinct");
        yield return Pipeline("SELECT DISTINCT nextval('s') FROM t LIMIT 0", "distinct");
        yield return Pipeline("SELECT DISTINCT 10/(id-2) FROM t LIMIT 1", "distinct") with { Fails = true };
        yield return Fallback("SELECT DISTINCT k FROM t ORDER BY id DESC LIMIT 1", "distinct");
        yield return Pipeline("SELECT EXISTS(SELECT record_call(id) FROM t)", "subquery-body");
        yield return Pipeline("SELECT EXISTS(SELECT a.id FROM t a JOIN u b ON a.k=b.k WHERE record_call(a.id)>0)", "subquery-body", "join-matches");
        yield return Pipeline("SELECT (SELECT a.id FROM t a JOIN u b ON a.k=b.k WHERE record_call(a.id)>0)", "subquery-body", "join-matches") with { Fails = true };
        yield return Fallback("SELECT EXISTS(SELECT record_call(a.id) FROM t a JOIN u b ON a.k=b.k)", "subquery-body", "join-matches");
        yield return Pipeline("SELECT (SELECT record_call(id) FROM t)", "subquery-body") with { Fails = true };
        yield return Fallback("SELECT (SELECT record_call(a.id) FROM t a JOIN u b ON a.k=b.k)", "subquery-body", "join-matches") with { Fails = true };
        yield return Pipeline("SELECT EXISTS(SELECT id FROM t LIMIT 0)", "subquery-body");
        yield return Pipeline("SELECT EXISTS(SELECT DISTINCT k FROM t)", "distinct") with { BaseOperators = ["subquery-body"] };
        yield return Pipeline("SELECT EXISTS(SELECT d.id FROM (SELECT record_call(id) AS id FROM t) d)", "subquery-body", "join-matches");
        yield return Pipeline("SELECT (SELECT d.id FROM (SELECT record_call(id) AS id FROM t) d)", "subquery-body", "join-matches") with { Fails = true };
        yield return Pipeline("SELECT id,(SELECT COUNT(*) FROM u WHERE u.k=t.k) FROM t", "group-input");
        yield return Pipeline("SELECT id,(SELECT u.id FROM u WHERE u.id=t.id) FROM t", "subquery-body");
        yield return Pipeline("SELECT DISTINCT id,(SELECT MIN(u.k) FROM u WHERE u.id=t.id) FROM t", "group-input") with { BaseOperators = ["distinct"] };
        yield return Pipeline("SELECT id,(SELECT DISTINCT u.k FROM u WHERE u.id=t.id) FROM t", "distinct");
        yield return Pipeline("SELECT id,(SELECT COUNT(*) FROM u a JOIN u b ON a.k=b.k WHERE a.k=t.k) FROM t", "group-input");
        yield return Pipeline("SELECT id,(SELECT COUNT(*) FROM u a JOIN u b ON a.k=b.k WHERE a.k=t.k LIMIT @take) FROM t", "group-input");
        yield return Pipeline("SELECT id,EXISTS(SELECT a.id FROM u a JOIN u b ON a.k=b.k WHERE a.k=t.k) FROM t", "subquery-body", "join-matches");
        yield return Fallback("SELECT nextval('s') FROM t GROUP BY k ORDER BY (SELECT MIN(u.k) FROM u WHERE u.k=t.k) LIMIT 0", "group-input");
        yield return Fallback("SELECT record_call(k) FROM t GROUP BY k ORDER BY (SELECT MIN(u.k) FROM u WHERE u.k=t.k) LIMIT 0", "group-input");
        yield return Fallback("SELECT COUNT(*) OVER() FROM (SELECT k,COUNT(*) FROM t GROUP BY k) d", "group-input");
        yield return Fallback("WITH RECURSIVE r(k) AS (SELECT 1 UNION ALL SELECT k+1 FROM r WHERE k<3) SELECT COUNT(*) FROM r", "group-input");
        yield return Pipeline("SELECT DISTINCT t.ci FROM t JOIN u ON t.k=u.k LIMIT 1 OFFSET 1", "join-matches", "distinct");
        // Both spellings reach the join; compare the surviving case/CHAR-space representative.
        yield return Pipeline("SELECT DISTINCT t.ci FROM t JOIN u ON t.k=u.k", "join-matches", "distinct");
        yield return Pipeline("SELECT DISTINCT t.c FROM t LEFT JOIN u ON t.k=u.k", "join-matches", "distinct");
        yield return Pipeline("SELECT DISTINCT t.c FROM t JOIN u ON t.k=u.k", "join-matches", "distinct");
        yield return Pipeline("SELECT DISTINCT t.j FROM t JOIN u ON t.k=u.k", "join-matches", "distinct");
        yield return Pipeline("SELECT a.id AS x FROM t a JOIN u b ON a.k=b.k ORDER BY x LIMIT 2", "join-matches");
        yield return Pipeline("SELECT a.id AS x FROM t a JOIN u b ON a.k=b.k ORDER BY 1 LIMIT 2", "join-matches");
        yield return Pipeline("SELECT a.id,b.id,c.id FROM t a JOIN u b ON a.k=b.k JOIN t c ON c.k=b.k WHERE b.id=2 LIMIT 3", "join-matches");
        yield return Pipeline("SELECT a.id,b.id,c.id FROM t a JOIN (u b JOIN t c ON c.k=b.k) ON a.k=b.k WHERE b.id=2 LIMIT 3", "join-matches");
        yield return Pipeline("SELECT COUNT(*) FROM t a JOIN (u b JOIN t c ON c.k=b.k) ON a.k=b.k WHERE b.id=2", "group-input");
        yield return Pipeline("SELECT COUNT(*) FROM (SELECT k FROM t) d JOIN u ON d.k=u.k", "group-input");
        yield return Pipeline("SELECT COUNT(*) FROM v JOIN u ON v.k=u.k", "group-input");
        yield return Pipeline("WITH c AS (SELECT k FROM t) SELECT COUNT(*) FROM c JOIN u ON c.k=u.k", "group-input");
        yield return Pipeline("SELECT EXISTS(SELECT id FROM t LIMIT 1 OFFSET 1)", "subquery-body");
        yield return Pipeline("SELECT (SELECT id FROM t LIMIT 1 OFFSET 1)", "subquery-body");
        yield return Pipeline("SELECT EXISTS(SELECT a.id FROM t a JOIN u b ON a.k=b.k LIMIT 1 OFFSET 1)", "subquery-body", "join-matches");
        yield return Pipeline("CREATE TABLE d AS SELECT DISTINCT k FROM t", "distinct") with { TableAs = true };
        yield return Pipeline("CREATE TABLE d AS SELECT DISTINCT t.k FROM t JOIN u ON t.k=u.k", "join-matches", "distinct") with { TableAs = true };
        yield return Pipeline("CREATE TABLE d AS SELECT k,COUNT(*) AS n FROM t GROUP BY k", "group-input") with { TableAs = true };
        yield return Pipeline("CREATE TABLE d AS SELECT t.id,u.k FROM t JOIN u ON t.k=u.k", "join-matches") with { TableAs = true };
        yield return Pipeline("CREATE TABLE d AS SELECT id,(SELECT u.id FROM u WHERE u.id=t.id) AS n FROM t", "subquery-body") with { TableAs = true };
    }

    private static string Cell(object? value) => value switch
    {
        null => "NULL",
        byte[] bytes => "bytes:" + Convert.ToHexString(bytes),
        _ => value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static async Task Execute(SqlDatabaseSession session, string sql)
        => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);

    private static async Task<object?[][]> Rows(SqlDatabaseSession session, string sql)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows.ToArray();
    }
}
