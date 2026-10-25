using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlHashJoinDifferentialTests
{
    public static IEnumerable<object[]> Cases()
    {
        var keys = new (string Left, string Right, object A, object B)[]
        {
            ("INT", "BIGINT", 1, 1L),
            ("INT", "DECIMAL", 1, 1.00m),
            ("BIGINT", "DECIMAL", 1L, 1.00m),
            ("REAL", "DOUBLE PRECISION", 1.25f, 1.25d),
            ("DOUBLE PRECISION", "INT", -0.0d, 0),
            ("REAL", "DOUBLE PRECISION", float.NaN, double.NaN),
            ("DATE", "TIMESTAMP", new DateOnly(2026, 10, 24), new DateTime(2026, 10, 24)),
            ("DATE", "TIMESTAMP WITH TIME ZONE", new DateOnly(2026, 10, 24), new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero)),
            ("TIMESTAMP", "TIMESTAMP WITH TIME ZONE", new DateTime(2026, 10, 24), new DateTimeOffset(2026, 10, 24, 2, 0, 0, TimeSpan.FromHours(2))),
            ("VARCHAR(20) COLLATE case_insensitive", "VARCHAR(20) COLLATE case_insensitive", "Alice", "ALICE"),
            ("VARCHAR(20) COLLATE case_accent_insensitive", "VARCHAR(20) COLLATE case_accent_insensitive", "café", "CAFE"),
            ("VARCHAR(20)", "VARCHAR(20)", "x ", "x "),
            ("VARCHAR(20)", "VARCHAR(20)", "x ", "x"),
            ("BOOLEAN", "BOOLEAN", true, true),
            ("UUID", "UUID", Guid.Parse("d7b83bd0-79eb-4ee6-84f3-9838c7b8e358"), Guid.Parse("d7b83bd0-79eb-4ee6-84f3-9838c7b8e358")),
            ("TIME", "TIME", new TimeOnly(12, 34, 56), new TimeOnly(12, 34, 56)),
        };
        foreach (var key in keys)
        {
            foreach (bool left in new[] { false, true }) { yield return [key.Left, key.Right, key.A, key.B, left]; }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash and nested-loop joins return identical typed-key pairs for INNER and LEFT")]
    [MemberData(nameof(Cases))]
    public async Task ExecuteAsync_TypedKeys_ShouldMatchNestedLoops(string leftType, string rightType, object a, object b, bool left)
    {
        // Arrange: duplicate matches, all-null keys and unmatched left rows share both plans.
        await using var engine = SqlDatabaseEngine.Create("hash-differential", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE l(id INT,k {leftType})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE r(id INT,k {rightType})", cancellationToken: CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["a"] = a, ["b"] = b };
        await session.ExecuteAsync("INSERT INTO l VALUES " + string.Join(',', Enumerable.Range(1, 80).Select(id => $"({id},{(id % 3 == 0 ? "NULL" : "@a")})")), parameters, CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO r VALUES " + string.Join(',', Enumerable.Range(1, 80).Select(id => $"({id},{(id % 5 == 0 ? "NULL" : "@b")})")), parameters, CancellationToken.None);
        var request = SqlQueryRequest.FromSql($"SELECT l.id,r.id FROM l {(left ? "LEFT" : "INNER")} JOIN r ON l.k=r.k ORDER BY l.id,r.id");
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var context = new SqlStatementContext(transaction, database.Coordinator);
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            var plan = new SqlPlanner(database.Catalog, null, database.Definitions.Functions).Plan(request.Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();
            var hash = executor.Cost(plan, context, CancellationToken.None).ShouldBeOfType<SqlJoinPlan>();
            hash.Steps[0].Hash.ShouldNotBeNull();
            var nested = hash with { Steps = hash.Steps.Select(step => step with { Hash = null }).ToArray() };

            // Act
            var expected = await ReadAsync(nested);
            var actual = await ReadAsync(hash);

            // Assert: compare every ordered pair, including duplicate matches and LEFT extensions.
            actual.ShouldBe(expected);
            if (left) { actual.ShouldNotBeEmpty(); }

            async Task<List<(int, int?)>> ReadAsync(SqlPlan selected)
            {
                await using var result = (DatabaseResultSet)await executor.ExecuteAsync(selected, context, CancellationToken.None);
                var rows = new List<(int, int?)>();
                await foreach (var row in result.GetRowsAsync(CancellationToken.None))
                { rows.Add((Convert.ToInt32(row.GetValue(0)), row.IsNull(1) ? null : Convert.ToInt32(row.GetValue(1)))); }
                return rows;
            }
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }
}
