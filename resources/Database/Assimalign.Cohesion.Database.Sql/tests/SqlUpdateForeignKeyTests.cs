using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlUpdateForeignKeyTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Update: foreign keys validate the complete pending replacement state (#1667)")]
    [InlineData(IsolationLevel.Snapshot, "NO ACTION", 0)]
    [InlineData(IsolationLevel.Snapshot, "NO ACTION", 1)]
    [InlineData(IsolationLevel.Snapshot, "NO ACTION", 2)]
    [InlineData(IsolationLevel.Snapshot, "RESTRICT", 0)]
    [InlineData(IsolationLevel.Snapshot, "RESTRICT", 1)]
    [InlineData(IsolationLevel.Snapshot, "RESTRICT", 2)]
    [InlineData(IsolationLevel.ReadCommitted, "NO ACTION", 0)]
    [InlineData(IsolationLevel.ReadCommitted, "NO ACTION", 1)]
    [InlineData(IsolationLevel.ReadCommitted, "NO ACTION", 2)]
    [InlineData(IsolationLevel.ReadCommitted, "RESTRICT", 0)]
    [InlineData(IsolationLevel.ReadCommitted, "RESTRICT", 1)]
    [InlineData(IsolationLevel.ReadCommitted, "RESTRICT", 2)]
    public async Task ExecuteAsync_PendingForeignKeys_ShouldValidateFinalState(IsolationLevel isolation, string action, int scenario)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("update-final-fks", new());
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync($"CREATE TABLE t (id INT PRIMARY KEY, parent INT REFERENCES t(id) ON DELETE {action})");
        string seed = scenario switch { 0 => "(1,NULL),(2,NULL)", 1 => "(1,1)", _ => "(1,2),(2,1)" };
        await session.ExecuteAsync("INSERT INTO t VALUES " + seed);
        await using var transaction = await session.BeginTransactionAsync(isolation, CancellationToken.None);
        string sql = scenario switch
        {
            0 => "UPDATE t SET id = CASE WHEN id = 1 THEN 10 ELSE id END, parent = CASE WHEN id = 2 THEN 1 ELSE parent END",
            1 => "UPDATE t SET id = 10, parent = 10",
            _ => "UPDATE t SET id = 3 - id, parent = 3 - parent"
        };

        // Act
        if (scenario == 0) { await SqlConstraintAssertions.ThrowAsync(async () => await session.ExecuteAsync(sql)); }
        else { (await session.ExecuteAsync(sql)).AffectedCount.ShouldBe(scenario == 1 ? 1 : 2); }

        // Assert: even the failed statement leaves its explicit transaction usable and unchanged.
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id, parent FROM t ORDER BY id");
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add([row.GetValue(0), row.GetValue(1)]); }
        object?[][] expected = scenario switch { 0 => [[1, null], [2, null]], 1 => [[10, 10]], _ => [[1, 2], [2, 1]] };
        rows.Count.ShouldBe(expected.Length);
        for (int i = 0; i < rows.Count; i++) { rows[i].ShouldBe(expected[i]); }
        await transaction.CommitAsync(CancellationToken.None);
    }
}