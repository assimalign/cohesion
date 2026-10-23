using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlNestedAnalysisTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - EXPLAIN ANALYZE: correlated descendants do not inflate their parent's candidate count")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CorrelatedWork_ShouldExcludeDescendantCandidates(bool joins)
    {
        await using var engine = SqlDatabaseEngine.Create("nested-analysis", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "outer_t", "outer_r", "inner_t", "inner_r" })
        {
            await session.ExecuteAsync($"CREATE TABLE {table}(id INT);", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync($"INSERT INTO {table} VALUES (1),(2);", cancellationToken: CancellationToken.None);
        }
        string query = joins
            ? "SELECT o.id,(SELECT COUNT(*) FROM inner_t i JOIN inner_r r ON i.id=r.id WHERE i.id<=o.id) AS n FROM outer_t o JOIN outer_r p ON o.id=p.id ORDER BY o.id;"
            : "SELECT o.id,(SELECT COUNT(*) FROM inner_t i WHERE i.id<=o.id) AS n FROM outer_t o ORDER BY o.id;";
        for (int execution = 0; execution < 2; execution++)
        {
            await using var description = (DatabaseResultSet)await session.ExecuteAsync("EXPLAIN ANALYZE " + query, cancellationToken: CancellationToken.None);
            var nodes = new List<(int? Parent, string Operator, string? Target, long? Actual)>();
            await foreach (var row in description.GetRowsAsync(CancellationToken.None))
            {
                nodes.Add((row.IsNull(1) ? null : row.GetInt32(1), row.GetString(2).ShouldNotBeNull(),
                    row.IsNull(3) ? null : row.GetString(3), row.IsNull(7) ? null : row.GetInt64(7)));
            }
            var outer = nodes.Single(node => node.Parent == 0 && node.Operator == (joins ? "HashJoin" : "Select"));
            outer.Actual.ShouldBe(2, "The outer operator has two candidates; correlated child work belongs to its own nodes.");
            nodes.Single(node => node.Operator == "TableScan" && node.Target == "dbo.outer_t").Actual.ShouldBe(2);
            var inner = joins ? nodes.Single(node => node.Operator == "HashJoin" && node.Parent != 0)
                : nodes.Single(node => node.Operator == "Select" && node.Target == "dbo.inner_t");
            inner.Actual.ShouldBe(4, "Two inner candidates are examined on each of two correlated invocations.");
            session.LastStatementMetrics.ShouldNotBeNull().RecordsExamined.ShouldBe(joins ? 12 : 6);
            session.LastStatementMetrics.JoinCandidatePairs.ShouldBe(joins ? 6 : 0);
        }

        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        int expected = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            Convert.ToInt32(row.GetValue(0)).ShouldBe(++expected);
            Convert.ToInt64(row.GetValue(1)).ShouldBe(expected);
        }
        expected.ShouldBe(2);
    }
}
