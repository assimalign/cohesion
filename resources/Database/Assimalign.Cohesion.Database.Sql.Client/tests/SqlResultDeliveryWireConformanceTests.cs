using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlResultDeliveryWireConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Result delivery: oversized rows roll back RETURNING writes and preserve session reuse")]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task QueryAsync_OversizedRow_ShouldRollbackStatementAndKeepConnection(bool tcp, bool returning, bool explicitTransaction)
    {
        // Arrange: the first row fits, and the second has thirty-four 512 KiB columns.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        var serverSession = harness.Server.Sessions.ShouldHaveSingleItem();
        if (explicitTransaction)
        {
            await connection.ExecuteAsync("BEGIN", cancellationToken: timeout.Token);
            await connection.ExecuteAsync("INSERT INTO users(id, name) VALUES (9, 'pending')", cancellationToken: timeout.Token);
        }
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
        failure.Message.ShouldContain("17825928", Case.Sensitive);
        failure.Message.ShouldContain("16777216", Case.Sensitive);
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE045");
        failure.ConnectionUsable.ShouldBeTrue();
        connection.IsOpen.ShouldBeTrue();
        after.Count.ShouldBe(explicitTransaction ? 3 : 2);
        harness.Server.Sessions.ShouldHaveSingleItem().ShouldBeSameAs(serverSession);
        await connection.ExecuteAsync("UPDATE users SET name = 'reused' WHERE id = 1", cancellationToken: timeout.Token);
        (await connection.QueryAsync("SELECT name FROM users WHERE id = 1", cancellationToken: timeout.Token))[0].GetString(0).ShouldBe("reused");
        if (explicitTransaction)
        {
            await connection.ExecuteAsync("COMMIT", cancellationToken: timeout.Token);
            await using var observer = await harness.Client.ConnectAsync(timeout.Token);
            (await observer.QueryAsync("SELECT id FROM users ORDER BY id", cancellationToken: timeout.Token)).Count.ShouldBe(3);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Result delivery: SELECT and RETURNING exceed 64 MiB without an aggregate cap")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_ResultAbove64MiB_ShouldSucceed(bool returning)
    {
        // Arrange: sixty-five 1 MiB strings exceed the former cap; every row fits one frame.
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(CancellationToken.None);
        await connection.ExecuteAsync("CREATE TABLE large_results(id INT)", cancellationToken: CancellationToken.None);
        const string source = "WITH c AS (SELECT 1 AS n UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 " +
            "UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) ";
        string statement = returning
            ? source + "INSERT INTO large_results SELECT (a.n - 1) * 9 + b.n FROM c a CROSS JOIN c b LIMIT 65 RETURNING @payload AS payload"
            : source + "SELECT @payload AS payload FROM c a CROSS JOIN c b LIMIT 65";

        // Act
        var rows = await connection.QueryAsync(statement, new Dictionary<string, object?> { ["payload"] = new string('x', 1048576) }, cancellationToken: CancellationToken.None);

        // Assert: the deterministic row count and payload lengths exceed 64 MiB without timing checks.
        rows.Count.ShouldBe(65);
        long payloadBytes = 0;
        foreach (var row in rows)
        {
            string payload = row.GetString(0);
            payload.Length.ShouldBe(1048576);
            payload[0].ShouldBe('x');
            payload[^1].ShouldBe('x');
            payloadBytes += payload.Length;
        }
        payloadBytes.ShouldBeGreaterThan(64L * 1024 * 1024);
        (await connection.QueryAsync("SELECT id FROM large_results", cancellationToken: CancellationToken.None)).Count.ShouldBe(returning ? 65 : 0);
        (await connection.QueryAsync("SELECT 42", cancellationToken: CancellationToken.None))[0].GetInt32(0).ShouldBe(42);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Result header: a repeated MiB CTE alias is a reusable statement error")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_OversizedHeader_ShouldFailStatementAndKeepConnection(bool tcp)
    {
        // Arrange: Codex's 1,048,885-character probe produces a 17,825,953-byte header.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        var serverSession = harness.Server.Sessions.ShouldHaveSingleItem();
        string joins = string.Join(" CROSS JOIN ", Enumerable.Range(1, 17).Select(index => $"c x{index}"));
        string statement = "WITH c AS (SELECT 1 AS \"" + new string('a', 1048576) + "\") SELECT * FROM " + joins;
        statement.Length.ShouldBe(1048885);

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync(statement, cancellationToken: timeout.Token));

        // Assert
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldStartWith("COHSQLE045:", Case.Sensitive);
        failure.Message.ShouldContain("17825953", Case.Sensitive);
        failure.ConnectionUsable.ShouldBeTrue();
        connection.IsOpen.ShouldBeTrue();
        (await connection.QueryAsync("SELECT 42", cancellationToken: timeout.Token))[0].GetInt32(0).ShouldBe(42);
        harness.Server.Sessions.ShouldHaveSingleItem().ShouldBeSameAs(serverSession);
    }
}
