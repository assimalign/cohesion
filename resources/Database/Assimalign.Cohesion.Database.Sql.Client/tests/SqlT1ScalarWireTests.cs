using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlT1ScalarWireTests
{
    public static IEnumerable<object[]> Cases() =>
    [
        ["REVERSE('A😀b')", "b😀A", DatabaseType.String],
        ["ROUND(2.5)", 3m, DatabaseType.Decimal],
        ["NULLIF('x', 'y')", "x", DatabaseType.String],
        ["DATE_TRUNC('month', DATE '2024-03-04')", new DateOnly(2024, 3, 1), DatabaseType.Date],
        ["TIMESTAMP '2024-03-04' + INTERVAL '1' DAY", new DateTime(2024, 3, 5), DatabaseType.DateTime],
        ["GEN_RANDOM_UUID()", typeof(Guid), DatabaseType.Guid],
    ];

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - T1 scalars: each family retains values and result metadata over the wire")]
    [MemberData(nameof(Cases))]
    public async Task Query_EveryScalarFamily_ShouldPreserveValueAndType(string expression, object expected, DatabaseType type)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        var result = await connection.QueryAsync($"SELECT {expression} FROM users WHERE id = 1;", cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.Columns.Single().Type.ShouldBe(type);
        var value = result.ShouldHaveSingleItem().GetValue(0);
        if (expected is Type) { (value.ShouldBeOfType<Guid>().ToByteArray()[7] >> 4).ShouldBe(4); }
        else { value.ShouldBe(expected); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Client] - T1 clock: UTC statement time lies between client reads and remains fixed inside BEGIN")]
    public async Task Query_TransactionClock_ShouldRemainFixedOnWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        var before = DateTimeOffset.UtcNow;
        var result = await connection.QueryAsync("SELECT CURRENT_TIMESTAMP, NOW() FROM users;", cancellationToken: SqlClientTestHarness.Timeout());
        var after = DateTimeOffset.UtcNow;
        await connection.ExecuteAsync("BEGIN", cancellationToken: SqlClientTestHarness.Timeout());
        var first = await connection.QueryAsync("SELECT NOW() FROM users WHERE id = 1;", cancellationToken: SqlClientTestHarness.Timeout());
        var second = await connection.QueryAsync("SELECT CURRENT_TIMESTAMP FROM users WHERE id = 1;", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("ROLLBACK", cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.Columns.ShouldAllBe(column => column.Type == DatabaseType.DateTimeOffset);
        var instant = result[0].GetValue(0).ShouldBeOfType<DateTimeOffset>();
        instant.ShouldBeGreaterThanOrEqualTo(before);
        instant.ShouldBeLessThanOrEqualTo(after);
        instant.Offset.ShouldBe(TimeSpan.Zero);
        result.ShouldAllBe(row => Equals(row.GetValue(0), instant) && Equals(row.GetValue(1), instant));
        first.ShouldHaveSingleItem().GetValue(0).ShouldBe(second.ShouldHaveSingleItem().GetValue(0));
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Client] - T1 domain faults: SQRT failure is coded and the same connection remains usable")]
    public async Task Query_DomainFault_ShouldKeepConnectionUsable()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync("SELECT SQRT(-1) FROM users;", cancellationToken: SqlClientTestHarness.Timeout()));
        var next = await connection.QueryAsync("SELECT SIGN(-2) FROM users WHERE id = 1;", cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Diagnostics[0].Code.ShouldBe("CS02002");
        failure.ConnectionUsable.ShouldBeTrue();
        next.ShouldHaveSingleItem().GetValue(0).ShouldBe(-1L);
    }
}
