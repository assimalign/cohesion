using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlJsonWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - JSON: extraction, predicates, scalar casts and refusals cross InMemory and TCP (#1722)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_JsonAccess_ShouldPreserveValuesAndConnectionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        var command = new SqlCommand("SELECT CAST(@doc AS JSON)->'a'->>0, CAST(@doc AS JSONB)->'a'->-1, CAST(@doc AS JSON)->'n', CAST(@doc AS JSON)->>'n', CAST(@doc AS JSON)->'missing', @doc IS JSON OBJECT, 'broken' IS NOT JSON, NULL IS JSON, CAST('12.5' AS JSONB)::INT, CAST('true' AS JSON)::BOOLEAN, CAST(CAST('\"x\"' AS JSON) AS TEXT)");
        command.Parameters.Add("doc", "{\"a\":[\"café\",null,3],\"n\":null}");
        // Act
        var row = (await connection.QueryAsync(command, SqlClientTestHarness.Timeout())).ShouldHaveSingleItem();
        // Assert
        row[0].ShouldBe("café");
        row[1].ShouldBeOfType<byte[]>().ShouldBe(Encoding.UTF8.GetBytes("3"));
        row[2].ShouldBe("null"); row[3].ShouldBeNull(); row[4].ShouldBeNull();
        row[5].ShouldBe(true); row[6].ShouldBe(true); row[7].ShouldBeNull();
        row[8].ShouldBe(13); row[9].ShouldBe(true); row[10].ShouldBe("\"x\"");
        foreach (var (sql, code) in new[]
        {
            ("SELECT 1 -> 'a'", "CS01067"),
            ("SELECT CAST('{}' AS JSON)::INT", "CS02079"),
            ("SELECT CAST('broken' AS JSON)->'a'", "CS02022"),
            ("SELECT CAST('1e100' AS JSONB)::INT", "CS02002"),
        })
        {
            var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
            error.ConnectionUsable.ShouldBeTrue();
        }
        await connection.ExecuteAsync("CREATE TABLE j(doc JSON)", cancellationToken: SqlClientTestHarness.Timeout());
        var invalid = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync("INSERT INTO j VALUES('broken')", cancellationToken: SqlClientTestHarness.Timeout()));
        invalid.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02078");
        invalid.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT COUNT(*) FROM j", cancellationToken: SqlClientTestHarness.Timeout())).ShouldHaveSingleItem()[0].ShouldBe(0L);
    }
}
