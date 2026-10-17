using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlTypedLiteralTests
{
    public static IEnumerable<object[]> Literals() =>
    [
        ["DATE", "DATE '2024-01-01'", DatabaseType.Date, new DateOnly(2024, 1, 1)],
        ["TIME", "TIME '10:20:30.1234567'", DatabaseType.Time, new TimeOnly(10, 20, 30).Add(TimeSpan.FromTicks(1234567))],
        ["TIMESTAMP", "TIMESTAMP '2024-01-01 10:00:00'", DatabaseType.DateTime, new DateTime(2024, 1, 1, 10, 0, 0)],
        ["TIMESTAMPTZ", "TIMESTAMPTZ '2024-01-01 12:00:00+02:00'", DatabaseType.DateTimeOffset, new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.FromHours(2))],
        ["INTERVAL", "INTERVAL '2 days 3 hours'", DatabaseType.TimeSpan, TimeSpan.FromHours(51)],
        ["BINARY", "X'00ff'", DatabaseType.Binary, new byte[] { 0, 255 }],
        ["TEXT", "N'héllo'", DatabaseType.String, "héllo"],
    ];

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Typed literals: wire values and metadata match stored values")]
    [MemberData(nameof(Literals))]
    public async Task QueryAsync_TypedLiteral_ShouldRoundTripRuntimeTypeAndMetadata(string type, string literal, DatabaseType metadata, object expected)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync($"CREATE TABLE typed (v {type} DEFAULT {literal});", cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        await connection.ExecuteAsync($"INSERT INTO typed VALUES ({literal}), (DEFAULT);", cancellationToken: SqlClientTestHarness.Timeout());
        var stored = await connection.QueryAsync("SELECT v FROM typed;", cancellationToken: SqlClientTestHarness.Timeout());
        var projected = await connection.QueryAsync($"SELECT {literal} FROM users WHERE id = 1;", cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        stored.Columns.Single().Type.ShouldBe(metadata);
        projected.Columns.Single().Type.ShouldBe(metadata);
        stored.Count.ShouldBe(2);
        foreach (var row in stored.Append(projected.ShouldHaveSingleItem()))
        {
            object? value = row.GetValue(0);
            value.ShouldNotBeNull().GetType().ShouldBe(expected.GetType());
            if (expected is byte[] bytes) { value.ShouldBeOfType<byte[]>().ShouldBe(bytes); }
            else { value.ShouldBe(expected); }
            if (value is DateTime clock) { clock.Kind.ShouldBe(DateTimeKind.Unspecified); }
            if (value is DateTimeOffset instant) { instant.Offset.ShouldBe(TimeSpan.FromHours(2)); }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Typed literals: invalid and unsupported literals retain parse diagnostics")]
    [InlineData("DATE '2024-02-30'", "SQL0008")]
    [InlineData("INTERVAL '1 year'", "COHDBL001")]
    [InlineData("INTERVAL 'P1M'", "COHDBL001")]
    public async Task QueryAsync_InvalidLiteral_ShouldReturnParseFailureAndKeepConnection(string literal, string code)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync($"SELECT {literal};", cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        error.Kind.ShouldBe(SqlClientErrorKind.ParseFailure);
        error.Message.ShouldContain(code, Case.Sensitive);
        error.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT 1 FROM users WHERE id = 1;", cancellationToken: SqlClientTestHarness.Timeout())).ShouldHaveSingleItem().GetInt64(0).ShouldBe(1L);
    }
}
