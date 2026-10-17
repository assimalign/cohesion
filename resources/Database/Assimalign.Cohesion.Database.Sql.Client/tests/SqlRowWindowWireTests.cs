using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>Verifies Int64 LIMIT/OFFSET counts through the SQL server and typed client.</summary>
public sealed class SqlRowWindowWireTests
{
    /// <summary>Returns the same row window for stored, joined, system and grouped relations.</summary>
    /// <param name="query">The ordered query producing three rows.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - LIMIT/OFFSET: Int64 counts preserve every plan shape over the wire")]
    [InlineData("SELECT id FROM window_rows ORDER BY id")]
    [InlineData("SELECT w.id FROM window_rows w INNER JOIN window_peers p ON w.id = p.id ORDER BY w.id")]
    [InlineData("SELECT ORDINAL_POSITION FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'window_rows' ORDER BY ORDINAL_POSITION")]
    [InlineData("SELECT id FROM window_rows GROUP BY id ORDER BY id")]
    public async Task QueryAsync_Int64RowWindow_ShouldReturnExpectedRows(string query)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE window_rows (id INT, category INT, label TEXT)", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO window_rows VALUES (3, 1, 'c'), (1, 1, 'a'), (2, 2, 'b')", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE window_peers (id INT)", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO window_peers VALUES (1), (2), (3)", cancellationToken: SqlClientTestHarness.Timeout());
        (string Clause, long[] Expected)[] windows =
        [
            ("", [1, 2, 3]),
            ("LIMIT 0", []),
            ("LIMIT 1", [1]),
            ("LIMIT 2 OFFSET 1", [2, 3]),
            ("LIMIT 2147483647", [1, 2, 3]),
            ("LIMIT 2147483648", [1, 2, 3]),
            ("LIMIT 4294967295", [1, 2, 3]),
            ("LIMIT 4294967296", [1, 2, 3]),
            ("LIMIT 4294967297", [1, 2, 3]),
            ("LIMIT 9223372036854775807", [1, 2, 3]),
            ("OFFSET 0", [1, 2, 3]),
            ("OFFSET 3", []),
            ("OFFSET 2147483647", []),
            ("OFFSET 2147483648", []),
            ("OFFSET 4294967295", []),
            ("OFFSET 4294967296", []),
            ("OFFSET 4294967297", []),
            ("OFFSET 9223372036854775807", []),
            ("LIMIT 9223372036854775807 OFFSET 1", [2, 3]),
            ("LIMIT 1 OFFSET 4294967297", []),
            ("LIMIT 9223372036854775807 OFFSET 9223372036854775807", []),
        ];

        foreach (var (clause, expected) in windows)
        {
            // Act
            var rows = await connection.QueryAsync($"{query} {clause}", cancellationToken: SqlClientTestHarness.Timeout());

            // Assert
            rows.Select(row => Convert.ToInt64(row[0])).ShouldBe(expected, clause);
        }
    }

    /// <summary>Preserves count refusals and connection reuse through the wire path.</summary>
    /// <param name="clause">The count clause.</param>
    /// <param name="count">The invalid count.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - LIMIT/OFFSET: negative and fractional counts retain usable execution failures")]
    [InlineData("LIMIT", "-1")]
    [InlineData("LIMIT", "1.5")]
    [InlineData("OFFSET", "-1")]
    [InlineData("OFFSET", "1.5")]
    public async Task QueryAsync_InvalidRowWindow_ShouldRetainDiagnosticAndConnection(string clause, string count)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync($"SELECT id FROM users {clause} {count}", cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        // #1103 composes the structured engine code at the existing wire boundary.
        error.Message.ShouldBe($"COHSQLE018: {clause} requires a non-negative integer.");
        error.ConnectionUsable.ShouldBeTrue();
        var rows = await connection.QueryAsync("SELECT id FROM users ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout());
        rows.Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
    }
}
