using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed partial class SqlSelectWideMemoryTests
{
    private readonly ITestOutputHelper _output;
    public SqlSelectWideMemoryTests(ITestOutputHelper output) { _output = output; }

    public static IEnumerable<object[]> Buffers()
    {
        string[] queries =
        [
            "SELECT @p AS p FROM t",
            "SELECT @p AS p FROM t ORDER BY k,p",
            "SELECT @p AS p FROM t ORDER BY k DESC,p LIMIT 2",
            "SELECT DISTINCT @p AS p,k FROM t",
            "SELECT @p AS p FROM t CROSS JOIN u",
            "SELECT d.p FROM t CROSS JOIN (SELECT @p AS p,k FROM u) d",
            "SELECT d.p FROM (SELECT @p AS p,k FROM t) d RIGHT JOIN u ON d.k>=u.k",
            "SELECT @p,k,COUNT(*) FROM t GROUP BY @p,k",
            "SELECT COUNT(DISTINCT @p) FROM t",
            "SELECT k,COUNT(DISTINCT @p) FROM t GROUP BY k",
            "SELECT MAX(@p ORDER BY k) FROM t",
            "SELECT k,MAX(@p ORDER BY k) FROM t GROUP BY k",
            "SELECT k,MIN(@p) FROM t GROUP BY k",
            "SELECT @p,k FROM t UNION SELECT @p,k FROM u",
            "SELECT @p,k FROM t INTERSECT SELECT @p,k FROM u",
            "WITH c AS (SELECT @p AS p,k FROM t) SELECT p FROM c ORDER BY k,p",
            "SELECT @p IN (SELECT @p FROM t)",
            "SELECT k,@p IN (SELECT @p FROM u WHERE u.k=t.k) FROM t",
            "SELECT nextval('s'),@p AS p,k FROM t ORDER BY k,p",
            "SELECT nextval('s'),@p AS p,(SELECT u.k FROM u WHERE u.k=t.k),k FROM t ORDER BY k,p",
            "CREATE TABLE dst AS SELECT @p AS p,k FROM t ORDER BY k,p",
            "SELECT @p,@p,@p FROM t ORDER BY k,@p",
            "SELECT b.doc FROM t CROSS JOIN b ORDER BY t.k,b.doc",
            "SELECT doc FROM equal_text ORDER BY k,doc",
            "SELECT CAST(@p AS CHAR(200)) FROM t ORDER BY k",
        ];
        long[][] peaks =
        [
            [680, 616, 680, 616],
            [896, 760, 896, 760],
            [760, 624, 760, 624],
            [1696, 1592, 1696, 1560],
            [1064, 1000, 1688, 1624],
            [2264, 2200, 2920, 2856],
            [2656, 2592, 3216, 3072],
            [2488, 2344, 2776, 2632],
            [1192, 1096, 1480, 1384],
            [3008, 2872, 3296, 3160],
            [2632, 2560, 2920, 2848],
            [4688, 4584, 4976, 4872],
            [2312, 2208, 2600, 2496],
            [2496, 2392, 2496, 2392],
            [4408, 4304, 4408, 4304],
            [1696, 1600, 2496, 2360],
            [1192, 1184, 1192, 1184],
            [1184, 1048, 1184, 1048],
            [1768, 1624, 1768, 1624],
            [1944, 1824, 1944, 1824],
            [2134, 1868, 2134, 1868],
            [944, 792, 944, 792],
            [1448, 1312, 1696, 1560],
            [1872, 1248, 1872, 1248],
            [3216, 2144, 3216, 2144],
        ];
        for (int i = 0; i < queries.Length; i++)
        {
            yield return [i, queries[i], false, peaks[i][0], peaks[i][1]];
            yield return [i, queries[i], true, peaks[i][2], peaks[i][3]];
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - wide SELECT memory: each buffer deduplicates identities and releases at every exit (#1776)")]
    [MemberData(nameof(Buffers))]
    public async Task ExecuteAsync_WideBuffer_ShouldPinAdmissionAndUnwind(int shape, string sql, bool forceBase, long expectedPeak, long expectedRefusal)
    {
        // Arrange / Act
        var success = await Run(1_048_576, 0, false);
        var exact = await Run(success.Peak, 0, false);
        var refused = await Run(success.Peak - 1, 0, false);
        _output.WriteLine($"WIDE_BUFFER|{shape}|{forceBase}|{success.Peak}|{refused.Peak}|rows={success.Rows}|heap={success.Heap}|ratio={success.Peak / (double)success.Heap:F4}");

        // Assert
        success.Code.ShouldBeNull();
        exact.Code.ShouldBeNull();
        exact.Rows.ShouldBe(success.Rows);
        refused.Code.ShouldBe("CS05067");
        success.Peak.ShouldBe(expectedPeak);
        refused.Peak.ShouldBe(expectedRefusal);
        for (int point = 1; point <= success.Reservations; point++)
        {
            (await Run(1_048_576, point, false)).Peak.ShouldBe(success.Trace[point - 1]);
            (await Run(1_048_576, point, true)).Peak.ShouldBe(success.Trace[point - 1]);
        }

        async Task<(long Peak, string? Code, int Rows, int Reservations, long Heap, List<long> Trace)> Run(long budget, int failAt, bool cancel)
        {
            var builder = SqlDatabaseEngine.CreateBuilder("wide-buffer");
            builder.Options.StatementMemoryBudget = budget;
            await using var engine = await builder.BuildAsync(CancellationToken.None);
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            foreach (string command in new[]
            {
                "CREATE SEQUENCE s", "CREATE TABLE t(k INT)", "INSERT INTO t VALUES(1),(2),(3)",
                "CREATE TABLE u(k INT)", "INSERT INTO u VALUES(1),(2)",
                "CREATE TABLE b(doc TEXT)", $"INSERT INTO b VALUES('{new string('x', 200)}')",
                "CREATE TABLE equal_text(k INT,doc TEXT)", $"INSERT INTO equal_text VALUES(1,'{new string('x', 200)}'),(2,'{new string('x', 200)}'),(3,'{new string('x', 200)}')",
            }) { await session.ExecuteAsync(command, cancellationToken: CancellationToken.None); }
            await session.ExecuteAsync("SELECT 0", cancellationToken: CancellationToken.None);
            database.Definitions.ForceSelectRetentionBasePath = forceBase;
            long before = GC.GetTotalMemory(true), retainedHeap = 0;
            string payload = new('x', 200);
            var parameters = new Dictionary<string, object?> { ["p"] = payload };
            int reservations = 0, count = 0;
            var trace = new List<long>();
            string? code = null;
            using var cancellation = new CancellationTokenSource();
            var fault = new InvalidOperationException("wide-buffer-fault");
            try
            {
                SqlStatementMetrics.AfterMemoryReservation = (metrics, _) =>
                {
                    reservations++;
                    trace.Add(metrics.PeakHashMemoryBytes);
                    if (failAt == 0) { retainedHeap = GC.GetTotalMemory(true) - before; }
                    if (reservations != failAt) { return; }
                    if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
                    throw fault;
                };
                async Task Execute()
                {
                    var result = await session.ExecuteAsync(sql, parameters, cancellation.Token);
                    if (result is not DatabaseResultSet rows) { count = (int)result.AffectedCount; return; }
                    await using (rows)
                    { await foreach (var row in rows.GetRowsAsync(cancellation.Token)) { count++; } }
                }
                if (failAt != 0)
                {
                    if (cancel) { await Should.ThrowAsync<OperationCanceledException>(Execute); }
                    else { (await Should.ThrowAsync<InvalidOperationException>(Execute)).ShouldBeSameAs(fault); }
                }
                else
                {
                    try { await Execute(); }
                    catch (DatabaseException error) { code = error.Diagnostics[0].Code; }
                }
            }
            finally { SqlStatementMetrics.AfterMemoryReservation = null; }
            var metrics = session.LastStatementMetrics.ShouldNotBeNull();
            metrics.HashMemoryBytes.ShouldBe(0);
            metrics.PlainSelectMemoryBytes.ShouldBe(0);
            return (metrics.PeakHashMemoryBytes, code, count, reservations, retainedHeap, trace);
        }
    }
}
