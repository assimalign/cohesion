using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlIlikeCastWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - ILIKE and postfix CAST: values and failures cross both transports (#1721)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_Operators_ShouldPreserveValuesAndConnectionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        var command = new SqlCommand("SELECT @s ILIKE 'aX%' ESCAPE 'X', @s NOT ILIKE 'b%', NULL ILIKE '%', -'7'::INT, '7'::TEXT::INT, 'É' COLLATE case_accent_insensitive ILIKE 'e'");
        command.Parameters.Add("s", "A%");
        // Act
        var row = (await connection.QueryAsync(command, SqlClientTestHarness.Timeout())).ShouldHaveSingleItem();
        // Assert
        row[0].ShouldBe(true); row[1].ShouldBe(true); row[2].ShouldBeNull();
        row[3].ShouldBe(-7L); row[4].ShouldBe(7); row[5].ShouldBe(true);
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync("SELECT 'bad'::INT", cancellationToken: SqlClientTestHarness.Timeout()));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02022");
        error.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT 1", cancellationToken: SqlClientTestHarness.Timeout())).ShouldHaveSingleItem()[0].ShouldBe(1);
    }
}
