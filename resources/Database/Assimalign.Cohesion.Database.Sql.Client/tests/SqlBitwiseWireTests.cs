using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlBitwiseWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Bitwise: typed values and binding failures cross InMemory and TCP (#1721)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_Bitwise_ShouldPreserveTypesAndConnectionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        var command = new SqlCommand("SELECT @s & 3::SMALLINT, @i | 2, @b # 7, ~@s, @i << 32, @b >> 65, NULL & @i, 1 | 2 & 3, ~1 + 2, ~NULL");
        command.Parameters.Add("s", (short)7);
        command.Parameters.Add("i", 5);
        command.Parameters.Add("b", -8L);
        // Act
        var row = (await connection.QueryAsync(command, SqlClientTestHarness.Timeout())).ShouldHaveSingleItem();
        // Assert
        row[0].ShouldBe((short)3); row[0]!.GetType().ShouldBe(typeof(short));
        row[1].ShouldBe(7); row[1]!.GetType().ShouldBe(typeof(int));
        row[2].ShouldBe(-1L); row[2]!.GetType().ShouldBe(typeof(long));
        row[3].ShouldBe((short)-8); row[4].ShouldBe(5); row[5].ShouldBe(-4L);
        row[6].ShouldBeNull(); row[7].ShouldBe(3); row[8].ShouldBe(-4L); row[9].ShouldBeNull();
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync("SELECT 1 & TRUE", cancellationToken: SqlClientTestHarness.Timeout()));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02077");
        error.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT 1", cancellationToken: SqlClientTestHarness.Timeout())).ShouldHaveSingleItem()[0].ShouldBe(1);
    }
}
