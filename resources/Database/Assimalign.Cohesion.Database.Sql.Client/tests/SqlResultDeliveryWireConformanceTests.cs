using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlResultDeliveryWireConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Result delivery: oversized rows discard partial results and preserve session reuse and committed RETURNING writes")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task QueryAsync_OversizedRow_ShouldDiscardPartialRowsAndKeepAutocommitWrites(bool tcp, bool returning)
    {
        // Arrange: the first row fits, and the second has thirty-four 512 KiB columns.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        var serverSession = harness.Server.Sessions.ShouldHaveSingleItem();
        string columns = string.Join(", ", Enumerable.Range(1, 34).Select(ordinal =>
            $"CASE WHEN id = {(returning ? 3 : 1)} THEN 'partial' ELSE RPAD('x', 524288, 'x') END AS c{ordinal}"));
        string statement = returning
            ? $"INSERT INTO users(id, name) VALUES (3, 'three'), (4, 'four') RETURNING {columns}"
            : $"SELECT {columns} FROM users ORDER BY id";

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync(statement, cancellationToken: timeout.Token));
        var after = await connection.QueryAsync("SELECT id FROM users ORDER BY id", cancellationToken: timeout.Token);

        // Assert: the failed QueryAsync returned no partial result, and the same session serves the next query.
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldStartWith("COHSQLE045:", Case.Sensitive);
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE045");
        failure.ConnectionUsable.ShouldBeTrue();
        connection.IsOpen.ShouldBeTrue();
        after.Count.ShouldBe(returning ? 4 : 2);
        harness.Server.Sessions.ShouldHaveSingleItem().ShouldBeSameAs(serverSession);
        await connection.ExecuteAsync("UPDATE users SET name = 'reused' WHERE id = 1", cancellationToken: timeout.Token);
        (await connection.QueryAsync("SELECT name FROM users WHERE id = 1", cancellationToken: timeout.Token))[0].GetString(0).ShouldBe("reused");
    }
}
