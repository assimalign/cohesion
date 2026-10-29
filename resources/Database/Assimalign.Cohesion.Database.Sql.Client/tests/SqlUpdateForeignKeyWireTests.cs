using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlUpdateForeignKeyWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Update: wire statements validate final foreign key state under both isolations (#1667)")]
    [InlineData(false, IsolationLevel.Snapshot, "NO ACTION", 0)]
    [InlineData(false, IsolationLevel.Snapshot, "NO ACTION", 1)]
    [InlineData(false, IsolationLevel.Snapshot, "NO ACTION", 2)]
    [InlineData(false, IsolationLevel.Snapshot, "RESTRICT", 0)]
    [InlineData(false, IsolationLevel.Snapshot, "RESTRICT", 1)]
    [InlineData(false, IsolationLevel.Snapshot, "RESTRICT", 2)]
    [InlineData(false, IsolationLevel.ReadCommitted, "NO ACTION", 0)]
    [InlineData(false, IsolationLevel.ReadCommitted, "NO ACTION", 1)]
    [InlineData(false, IsolationLevel.ReadCommitted, "NO ACTION", 2)]
    [InlineData(false, IsolationLevel.ReadCommitted, "RESTRICT", 0)]
    [InlineData(false, IsolationLevel.ReadCommitted, "RESTRICT", 1)]
    [InlineData(false, IsolationLevel.ReadCommitted, "RESTRICT", 2)]
    [InlineData(true, IsolationLevel.Snapshot, "NO ACTION", 0)]
    [InlineData(true, IsolationLevel.Snapshot, "NO ACTION", 1)]
    [InlineData(true, IsolationLevel.Snapshot, "NO ACTION", 2)]
    [InlineData(true, IsolationLevel.Snapshot, "RESTRICT", 0)]
    [InlineData(true, IsolationLevel.Snapshot, "RESTRICT", 1)]
    [InlineData(true, IsolationLevel.Snapshot, "RESTRICT", 2)]
    [InlineData(true, IsolationLevel.ReadCommitted, "NO ACTION", 0)]
    [InlineData(true, IsolationLevel.ReadCommitted, "NO ACTION", 1)]
    [InlineData(true, IsolationLevel.ReadCommitted, "NO ACTION", 2)]
    [InlineData(true, IsolationLevel.ReadCommitted, "RESTRICT", 0)]
    [InlineData(true, IsolationLevel.ReadCommitted, "RESTRICT", 1)]
    [InlineData(true, IsolationLevel.ReadCommitted, "RESTRICT", 2)]
    public async Task ExecuteAsync_PendingForeignKeys_ShouldValidateFinalState(bool tcp, IsolationLevel isolation, string action, int scenario)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync($"CREATE TABLE t (id INT PRIMARY KEY, parent INT REFERENCES t(id) ON DELETE {action} ON UPDATE {action})", cancellationToken: SqlClientTestHarness.Timeout(30));
        string seed = scenario switch { 0 => "(1,NULL),(2,NULL)", 1 => "(1,1)", _ => "(1,2),(2,1)" };
        await connection.ExecuteAsync("INSERT INTO t VALUES " + seed, cancellationToken: SqlClientTestHarness.Timeout(30));
        // SQL's BEGIN grammar has no isolation clause; use the existing server-session API
        // to choose isolation, then execute the entire UPDATE and result exchange over the wire.
        var session = (SqlDatabaseSession)harness.Server.Sessions.ShouldHaveSingleItem().DatabaseSession!;
        await using var transaction = await session.BeginTransactionAsync(isolation, SqlClientTestHarness.Timeout(30));
        string sql = scenario switch
        {
            0 => "UPDATE t SET id = CASE WHEN id = 1 THEN 10 ELSE id END, parent = CASE WHEN id = 2 THEN 1 ELSE parent END",
            1 => "UPDATE t SET id = 10, parent = 10",
            _ => "UPDATE t SET id = 3 - id, parent = 3 - parent"
        };

        // Act
        if (scenario == 0)
        {
            var failure = await Should.ThrowAsync<SqlClientException>(async () =>
                await connection.ExecuteAsync(sql, cancellationToken: SqlClientTestHarness.Timeout(30)));
            failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03039");
            failure.ConnectionUsable.ShouldBeTrue();
        }
        else { (await connection.ExecuteAsync(sql, cancellationToken: SqlClientTestHarness.Timeout(30))).ShouldBe(scenario == 1 ? 1 : 2); }

        // Assert
        var rows = await connection.QueryAsync("SELECT id, parent FROM t ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout(30));
        object?[][] expected = scenario switch { 0 => [[1, null], [2, null]], 1 => [[10, 10]], _ => [[1, 2], [2, 1]] };
        rows.Count.ShouldBe(expected.Length);
        for (int i = 0; i < rows.Count; i++) { new object?[] { rows[i][0], rows[i][1] }.ShouldBe(expected[i]); }
        await connection.ExecuteAsync("COMMIT", cancellationToken: SqlClientTestHarness.Timeout(30));
    }
}