using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSequenceFifthPassTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence sorting: LIMIT and deep OFFSET have the ordinary sort's uncharged accounting")]
    [InlineData(0, 0)][InlineData(0, 1)][InlineData(0, 2)]
    [InlineData(1, 0)][InlineData(1, 1)][InlineData(1, 2)]
    [InlineData(2, 0)][InlineData(2, 1)][InlineData(2, 2)]
    [InlineData(3, 0)][InlineData(3, 1)][InlineData(3, 2)]
    public async Task Execute_BoundedSequenceSortCorpus_ShouldMatchOrdinaryAccounting(int shape, int window)
    {
        // Arrange: low budgets reproduce the large default-budget failure with 256 rows.
        const int count = 256;
        await using var engine = SqlDatabaseEngine.Create("fifth-pass", new() { StatementMemoryBudget = shape == 3 ? 1048576 : 8192 });
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");
        await Exec(session, "CREATE TABLE staging(k INT)");
        await Exec(session, "CREATE TABLE u(k INT PRIMARY KEY)");
        await Exec(session, "CREATE TABLE dim(id BIGINT, k INT)");
        string values = string.Join(",", Enumerable.Range(1, count).Reverse().Select(i => $"({i})"));
        await Exec(session, "INSERT INTO staging VALUES " + values);
        await Exec(session, "INSERT INTO u VALUES " + values);
        string clauses = " FROM staging" + (shape > 0 ? " WHERE EXISTS (SELECT 1 FROM u WHERE u.k=staging.k)" : "") +
            (shape == 2 ? " GROUP BY k" : shape == 3 ? " GROUP BY GROUPING SETS ((k), ()) HAVING GROUPING(k)=0" : "") + " ORDER BY k";
        int offset = window == 2 ? 200 : 0;
        int delivered = window == 0 ? count : 1;
        string page = window == 0 ? " LIMIT 1000000" : window == 1 ? " LIMIT 1" : " LIMIT 1 OFFSET 200";
        await Exec(session, "INSERT INTO dim SELECT CAST(k AS BIGINT), k" + clauses + page);
        long ordinaryPeak = session.LastStatementMetrics!.PeakHashMemoryBytes;
        await Exec(session, "DELETE FROM dim");
        int retained = 0;
        SqlPlanExecutor.SequenceCandidatesRetained = value => retained = Math.Max(retained, value);
        try
        {
            // Act
            await Exec(session, "INSERT INTO dim SELECT nextval('s'), k" + clauses + page);
            var metrics = session.LastStatementMetrics!;
            var rows = await Rows(session, "SELECT id,k FROM dim ORDER BY k");

            // Assert: grouping-set state retains its own preexisting charge; sort adds none.
            rows.Length.ShouldBe(delivered);
            for (int i = 0; i < rows.Length; i++) { rows[i].ShouldBe(new object?[] { (long)(offset + i + 1), offset + i + 1 }); }
            metrics.PeakHashMemoryBytes.ShouldBe(ordinaryPeak);
            metrics.HashMemoryBytes.ShouldBe(0);
            if (shape > 0) { retained.ShouldBe(offset + delivered); }
            (await Rows(session, "SELECT nextval('s')"))[0][0].ShouldBe((long)(offset + delivered + 1));
            _output.WriteLine($"shape={shape}; window={window}; delivered={delivered}; offset={offset}; retained={retained}; ordinary peak={ordinaryPeak}; sequence peak={metrics.PeakHashMemoryBytes}; final grant=0");
        }
        finally { SqlPlanExecutor.SequenceCandidatesRetained = null; }
    }
}
