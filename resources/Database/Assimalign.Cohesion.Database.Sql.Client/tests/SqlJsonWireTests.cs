using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlJsonWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - SQL/JSON: statement budget refusal preserves InMemory/TCP connections (#1722)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_JsonPathBudget_ShouldRefuseAndRecoverAsync(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp, configureEngine: options => options.StatementMemoryBudget = 32_768);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        var command = new SqlCommand("SELECT JSON_QUERY(@doc,'$[*]' WITH WRAPPER NULL ON ERROR)");
        command.Parameters.Add("doc", "[" + string.Join(',', Enumerable.Repeat("1", 1000)) + "]");
        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(command, SqlClientTestHarness.Timeout()));
        // Assert
        failure.Diagnostics[0].Code.ShouldBe("CS05094");
        failure.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT JSON_EXISTS('{}','$')", cancellationToken: SqlClientTestHarness.Timeout())).ShouldHaveSingleItem()[0].ShouldBe(true);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - SQL/JSON: typed paths and behaviors cross InMemory and TCP (#1722)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_JsonPaths_ShouldPreserveTypesAndErrorsAsync(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        var command = new SqlCommand("SELECT JSON_VALUE(@doc,@path RETURNING INT), JSON_QUERY(@doc,'$.a[*]' WITH WRAPPER), JSON_EXISTS(@doc,'$.missing'), JSON_VALUE(@doc,'$.missing' DEFAULT 'empty' ON EMPTY), JSON_EXISTS(@doc,'strict $.missing' UNKNOWN ON ERROR), JSON_VALUE(NULL,'$'), JSON_VALUE('false','$' RETURNING BOOLEAN), JSON_QUERY('true','$' RETURNING BOOLEAN)");
        command.Parameters.Add("doc", "{\"a\":[1,null,3]}");
        command.Parameters.Add("path", "$.a[last]");
        // Act
        var row = (await connection.QueryAsync(command, SqlClientTestHarness.Timeout())).ShouldHaveSingleItem();
        // Assert
        row[0].ShouldBe(3);
        row[1].ShouldBeOfType<byte[]>().ShouldBe(Encoding.UTF8.GetBytes("[1, null, 3]"));
        row[2].ShouldBe(false); row[3].ShouldBe("empty"); row[4].ShouldBeNull(); row[5].ShouldBeNull();
        row[6].ShouldBe(false); row[7].ShouldBe(true);
        foreach (var (sql, code) in new[]
        {
            ("SELECT JSON_VALUE('broken','$' NULL ON ERROR)", "CS02078"),
            ("SELECT JSON_VALUE('{}','$.a' ERROR ON EMPTY)", "CS02083"),
            ("SELECT JSON_EXISTS('{}','strict $.a' ERROR ON ERROR)", "CS02082"),
            ("SELECT JSON_VALUE('[1,2]','$[*]' ERROR ON ERROR)", "CS05093"),
            ("SELECT JSON_VALUE('{}','$.type()')", "CS02081"),
            ("SELECT JSON_QUERY('{}','$'||'.a')", "CS01068"),
            ("SELECT JSON_QUERY('{}','$' PASSING 1 AS x)", "CS02081"),
        })
        {
            var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()));
            failure.Diagnostics[0].Code.ShouldBe(code);
            failure.ConnectionUsable.ShouldBeTrue();
        }
        (await connection.QueryAsync("SELECT JSON_VALUE('42','$' RETURNING INT)", cancellationToken: SqlClientTestHarness.Timeout())).ShouldHaveSingleItem()[0].ShouldBe(42);
    }

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
