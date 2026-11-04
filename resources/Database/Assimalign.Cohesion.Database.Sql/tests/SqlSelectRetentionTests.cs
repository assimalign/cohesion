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
        var parameters = new Dictionary<string, object?> { ["take"] = 2, ["skip"] = 1, ["key"] = 2 };
        await Execute("CREATE SEQUENCE s");
        await Execute("CREATE TABLE t(id INT PRIMARY KEY,k INT,c CHAR(6),ci TEXT COLLATE case_insensitive,doc TEXT,b BINARY)");
        await Execute("CREATE TABLE u(id INT PRIMARY KEY,k INT)");
        await Execute("INSERT INTO u VALUES(1,1),(2,2),(4,4)");
        await Execute("CREATE VIEW v AS SELECT id,k,c,ci,doc,b FROM t");
        var observed = new List<(string Operator, bool Pipeline, int Rows)>();
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
                    $"'{(id % 2 == 0 ? "Ab" : "aB")}','{text}',@bytes)",
                    new Dictionary<string, object?> { ["bytes"] = new byte[] { (byte)(id % 3), (byte)id } });
            }
            foreach (string sql in Corpus())
            {
                // Act / Assert: statement result, headers, sequence state and volatile call log.
                var baseline = await Capture(sql, true);
                if (!sql.Contains("10/(", StringComparison.Ordinal) &&
                    !sql.Contains("NEXT VALUE FOR", StringComparison.Ordinal) &&
                    !sql.StartsWith("SELECT (SELECT record_call", StringComparison.Ordinal) &&
                    !sql.StartsWith("SELECT (SELECT d.id", StringComparison.Ordinal) &&
                    !sql.StartsWith("SELECT (SELECT a.id", StringComparison.Ordinal))
                { baseline.ShouldNotContain("FAILED", sql); }
                var pipelined = await Capture(sql, false);
                pipelined.ShouldBe(baseline, $"seed={seed}; transport={transport}; {sql}");
                // Execute analysis separately: text, actual rows and work counters are observable too.
                (await Capture("EXPLAIN ANALYZE " + sql, false)).ShouldBe(await Capture("EXPLAIN ANALYZE " + sql, true), sql);
                (await Capture("EXPLAIN " + sql, false)).ShouldBe(await Capture("EXPLAIN " + sql, true), sql);
                statements += 6;
            }
        }
        // Every pipeline is reached, while ordered/correlated DISTINCT and volatile interleaving fall back.
        foreach (string name in new[] { "group-input", "join-matches", "distinct", "subquery-body" })
        {
            observed.ShouldContain(item => item.Operator == name && item.Pipeline, name);
            observed.ShouldContain(item => item.Operator == name && !item.Pipeline, name);
        }
        _output.WriteLine($"transport={transport}; differential statements={statements}");

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
                result.AddRange(rows.Columns.Select(column => $"{column.Name}:{column.Type}:{column.MaxLength}"));
                foreach (var row in rows)
                { result.Add(string.Join("|", Enumerable.Range(0, rows.Columns.Count).Select(i => Cell(row.GetValue(i))))); }
            }
            else
            {
                await using var rows = (DatabaseResultSet)await session.ExecuteAsync(sql, parameters, CancellationToken.None);
                result.Add("affected=" + rows.AffectedCount);
                result.AddRange(rows.Columns.Select(column => $"{column.Name}:{column.Type}:{column.MaxLength}:{column.IsNullable}"));
                await foreach (var row in rows.GetRowsAsync(CancellationToken.None))
                { result.Add(string.Join("|", Enumerable.Range(0, row.FieldCount).Select(i => Cell(row.GetValue(i))))); }
            }
            return result.ToArray();
        }

        async Task<string[]> Capture(string sql, bool forceBase)
        {
            database.Definitions.ForceSelectRetentionBasePath = forceBase;
            await Execute("ALTER SEQUENCE s RESTART WITH 1");
            calls.Clear();
            var captured = new List<string>();
            try { captured.AddRange(await Read(sql)); }
            catch (Exception failure) when (failure is DatabaseException or SqlClientException) { captured.Add("FAILED"); }
            if (transport == 0)
            {
                var metrics = session.LastStatementMetrics!;
                captured.Add($"work={metrics.RecordsExamined}:{metrics.JoinCandidatePairs}:{metrics.AccessPath}:{metrics.PeakOrderedRows}:" +
                    $"{metrics.PeakHashMemoryBytes}:{metrics.HashMemoryBytes}:" + string.Join(",", metrics.JoinAccessPaths ?? []));
            }
            captured.Add("calls=" + string.Join(",", calls));
            captured.AddRange(await Read("SELECT nextval('s')"));
            return captured.ToArray();
        }
    }

    private static IEnumerable<string> Corpus()
    {
        foreach (string key in new[] { "k", "c", "ci", "doc", "b" })
        {
            yield return $"SELECT {key},COUNT(*),MIN(b),MAX(b) FROM t GROUP BY {key}";
            yield return $"SELECT {key},SUM(id) FROM t GROUP BY {key} HAVING COUNT(*)>0 ORDER BY SUM(id) LIMIT @take OFFSET @skip";
            foreach (string page in new[] { "", " LIMIT 1", " LIMIT 0", " LIMIT @take OFFSET @skip" })
            { yield return $"SELECT DISTINCT {key} FROM t" + page; }
        }
        foreach (string join in new[] { "INNER", "LEFT", "RIGHT", "FULL" })
        {
            foreach (string page in new[] { "", " LIMIT 2 OFFSET 1", " LIMIT 0", " ORDER BY t.k LIMIT 2" })
            { yield return $"SELECT t.c,t.b,u.id FROM t {join} JOIN u ON t.id=u.id" + page; }
            yield return $"SELECT t.c,COUNT(*) FROM t {join} JOIN u ON t.k=u.k GROUP BY t.c";
        }
        yield return "SELECT COUNT(*) FROM t WHERE id<0";
        yield return "SELECT COUNT(*)";
        yield return "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES";
        yield return "SELECT COUNT(*) FROM t WHERE id=@key";
        yield return "SELECT COUNT(*) FROM t WHERE c LIKE 'a%'";
        yield return "SELECT SUM(JSON_VALUE('{\"k\":1}', '$.k' RETURNING INT)) FROM t";
        yield return "SELECT JSON_VALUE('{\"k\":1}', '$.k' RETURNING INT) FROM t a JOIN u b ON a.k=b.k LIMIT 2";
        yield return "SELECT k,COUNT(*) FROM v GROUP BY k";
        yield return "SELECT k,COUNT(*) FROM (SELECT k FROM t ORDER BY id) d GROUP BY k";
        yield return "SELECT a.id,b.id,c.id,d.id FROM t a FULL JOIN u b ON a.k=b.k JOIN t c ON c.id=b.id LEFT JOIN u d ON c.k=d.k LIMIT 2";
        yield return "SELECT a.id,b.id FROM t a JOIN u b ON a.k>b.k LIMIT 2";
        yield return "SELECT a.id,nextval('s') FROM t a JOIN u b ON a.k=b.k LIMIT 2";
        yield return "SELECT a.id,nextval('s') FROM t a JOIN u b ON a.k=b.k ORDER BY a.id LIMIT 2 OFFSET 1";
        yield return "SELECT record_call(a.id) FROM t a JOIN u b ON a.k=b.k ORDER BY a.id LIMIT 2";
        yield return "SELECT record_call(a.id) FROM t a JOIN u b ON a.k=b.k WHERE record_call(b.id)>0 LIMIT 2";
        yield return "SELECT SUM(record_call(id)) FROM t WHERE record_call(id)>0";
        yield return "SELECT sum_calls(id) FROM t WHERE record_call(id)>0";
        yield return "SELECT SUM(10/(id-2)) FROM t WHERE record_call(id)>0";
        yield return "SELECT SUM(record_call(id)) FROM t WHERE 10/(id-2)<>0";
        yield return "SELECT record_call(a.id) FROM t a JOIN u b ON a.k=b.k WHERE 10/(a.id-2)<>0";
        yield return "SELECT 10/(a.id-2) FROM t a JOIN u b ON a.k=b.k WHERE record_call(a.id)>0";
        yield return "SELECT a.id FROM t a JOIN u b ON a.k=b.k WHERE 10/(a.id-2)<>0 ORDER BY a.id LIMIT 2";
        yield return "SELECT 10/(a.id-2) FROM t a JOIN u b ON a.k=b.k WHERE record_call(a.id)>0 ORDER BY a.id LIMIT 2";
        yield return "SELECT 10/(a.id-2) FROM t a JOIN u b ON a.k=b.k WHERE 10/(a.id-5)<>0 ORDER BY a.id LIMIT 2";
        yield return "SELECT a.id FROM t a RIGHT JOIN u b ON record_call(a.id)=b.id WHERE 10/(b.id-2)<>0 ORDER BY a.id LIMIT 2";
        yield return "SELECT COUNT(*) FROM t a RIGHT JOIN u b ON record_call(a.id)=b.id WHERE 10/(b.id-2)<>0";
        yield return "SELECT a.id FROM t a FULL JOIN u b ON 10/(a.id-2)=b.id WHERE record_call(b.id)>0";
        yield return "SELECT COUNT(*) FROM t a FULL JOIN u b ON 10/(a.id-2)=b.id WHERE record_call(b.id)>0";
        yield return "SELECT a.id FROM t a JOIN u b ON a.k=b.k WHERE nextval('s')>0 LIMIT 1";
        yield return "SELECT COUNT(*) FROM t WHERE nextval('s')>0";
        yield return "SELECT a.id FROM t a JOIN u b ON a.k=b.k WHERE NEXT VALUE FOR s>0 LIMIT 1";
        yield return "SELECT COUNT(*) FROM t a JOIN u b ON a.k=b.k WHERE NEXT VALUE FOR s>0";
        yield return "SELECT DISTINCT k FROM t WHERE NEXT VALUE FOR s>0 LIMIT 1";
        yield return "SELECT EXISTS(SELECT k FROM t WHERE NEXT VALUE FOR s>0)";
        yield return "SELECT DISTINCT k FROM t WHERE nextval('s')>0 LIMIT 1";
        yield return "SELECT EXISTS(SELECT a.id FROM t a JOIN u b ON a.k=b.k WHERE nextval('s')>0)";
        yield return "SELECT DISTINCT nextval('s') FROM t LIMIT 1";
        yield return "SELECT DISTINCT nextval('s') FROM t LIMIT 0";
        yield return "SELECT DISTINCT 10/(id-2) FROM t LIMIT 1";
        yield return "SELECT DISTINCT k FROM t ORDER BY id DESC LIMIT 1";
        yield return "SELECT EXISTS(SELECT record_call(id) FROM t)";
        yield return "SELECT EXISTS(SELECT a.id FROM t a JOIN u b ON a.k=b.k WHERE record_call(a.id)>0)";
        yield return "SELECT (SELECT a.id FROM t a JOIN u b ON a.k=b.k WHERE record_call(a.id)>0)";
        yield return "SELECT EXISTS(SELECT record_call(a.id) FROM t a JOIN u b ON a.k=b.k)";
        yield return "SELECT (SELECT record_call(id) FROM t)";
        yield return "SELECT (SELECT record_call(a.id) FROM t a JOIN u b ON a.k=b.k)";
        yield return "SELECT EXISTS(SELECT id FROM t LIMIT 0)";
        yield return "SELECT EXISTS(SELECT DISTINCT k FROM t)";
        yield return "SELECT EXISTS(SELECT d.id FROM (SELECT record_call(id) AS id FROM t) d)";
        yield return "SELECT (SELECT d.id FROM (SELECT record_call(id) AS id FROM t) d)";
        yield return "SELECT id,(SELECT COUNT(*) FROM u WHERE u.k=t.k) FROM t";
        yield return "SELECT id,(SELECT u.id FROM u WHERE u.id=t.id) FROM t";
        yield return "SELECT DISTINCT id,(SELECT MIN(u.k) FROM u WHERE u.id=t.id) FROM t";
        yield return "SELECT nextval('s') FROM t GROUP BY k ORDER BY (SELECT MIN(u.k) FROM u WHERE u.k=t.k) LIMIT 0";
        yield return "SELECT COUNT(*) OVER() FROM t";
        yield return "WITH RECURSIVE r(k) AS (SELECT 1 UNION ALL SELECT k+1 FROM r WHERE k<3) SELECT COUNT(*) FROM r";
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
