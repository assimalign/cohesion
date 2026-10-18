using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlPredicateWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Predicates: truth tables and parameter ESCAPE cross both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_PredicateTruthTables_ShouldReturnBooleansAndUnknownAsync(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE truth (id INT, a INT, b INT, flag BOOLEAN)", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO truth VALUES (1,NULL,NULL,TRUE),(2,NULL,1,FALSE),(3,1,NULL,NULL),(4,1,1,TRUE),(5,1,2,FALSE)", cancellationToken: SqlClientTestHarness.Timeout());
        // Act
        var rows = await connection.QueryAsync("SELECT a IS DISTINCT FROM b, a IS NOT DISTINCT FROM b, flag IS TRUE, flag IS NOT TRUE, flag IS FALSE, flag IS NOT FALSE, flag IS UNKNOWN, flag IS NOT UNKNOWN FROM truth ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout());
        var command = new SqlCommand("SELECT '50%' LIKE '50!%' ESCAPE @e, 'x' LIKE 'x' ESCAPE @n, 'A%' LIKE 'aX%' ESCAPE 'X' COLLATE case_insensitive FROM users WHERE id = 1");
        command.Parameters.Add("e", "!");
        command.Parameters.Add("n", null);
        var escape = await connection.QueryAsync(command, SqlClientTestHarness.Timeout());
        // Assert
        rows.Select(row => Enumerable.Range(0, 8).Select(i => row[i]).ToArray()).ShouldBe(new object?[][]
        {
            [false,true,true,false,false,true,false,true],
            [true,false,false,true,true,false,false,true],
            [true,false,false,true,false,true,true,false],
            [false,true,true,false,false,true,false,true],
            [true,false,false,true,true,false,false,true],
        });
        escape.ShouldHaveSingleItem()[0].ShouldBe(true);
        escape[0][1].ShouldBeNull();
        escape[0][2].ShouldBe(true);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Predicates: planning and escape failures leave data and connection usable")]
    [InlineData("DELETE FROM users WHERE score IS TRUE", "COHSQLE003")]
    [InlineData("SELECT name LIKE 'a%' ESCAPE 'long' FROM users", "COHSQLE034")]
    [InlineData("SELECT name LIKE 'a!' ESCAPE '!' FROM users", "COHSQLE034")]
    [InlineData("SELECT name LIKE 'a!' ESCAPE name FROM users", "COHSQLE034")]
    public async Task QueryAsync_InvalidPredicate_ShouldReturnExecutionFailureAndKeepConnectionAsync(string sql, string code)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()));
        // Assert
        error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        error.Message.ShouldContain(code, Case.Sensitive);
        error.ConnectionUsable.ShouldBeTrue();
        var users = await connection.QueryAsync("SELECT id, name, score FROM users ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout());
        users.Select(row => (row[0], row[1], row[2])).ShouldBe(new (object?, object?, object?)[] { (1,"ada",100L),(2,"grace",200L) });
    }
}
