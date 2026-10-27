using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlT1StringLimitTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - T1 strings: every allocating string path refuses oversized input before expansion")]
    [InlineData("UPPER(@p)", false)]
    [InlineData("LOWER(@p)", false)]
    [InlineData("REVERSE(@p)", false)]
    [InlineData("TRIM(@p)", false)]
    [InlineData("LTRIM(@p)", false)]
    [InlineData("RTRIM(@p)", false)]
    [InlineData("SUBSTRING(@p FROM 1 FOR 1)", false)]
    [InlineData("LEFT(@p, 1)", false)]
    [InlineData("RIGHT(@p, 1)", false)]
    [InlineData("REPLACE(@p, 'x', 'y')", false)]
    [InlineData("LPAD(@p, 1)", false)]
    [InlineData("RPAD(@p, 1)", false)]
    [InlineData("CONCAT(@p)", false)]
    [InlineData("CONCAT_WS('', @p)", false)]
    [InlineData("CONCAT(@p)", true)]
    [InlineData("CONCAT_WS('', @p)", true)]
    public async Task Select_OversizedStringWork_ShouldReturnCodedFailure(string expression, bool binary)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("string-work-limits", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);
        object value = binary ? new byte[524288] : new string('x', 1048577);
        var parameters = new Dictionary<string, object?> { ["p"] = value };

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync($"SELECT {expression} FROM t", parameters, CancellationToken.None).AsTask());
        failure.Diagnostics[0].Code.ShouldBe("CS02002");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - T1 strings: exact limits succeed and empty padding keeps PostgreSQL behavior")]
    public async Task Select_StringLengthAtLimit_ShouldReturnCompleteUnicodeValues()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("string-limits", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);

        // Act
        await using var result = (await session.ExecuteAsync("SELECT CHAR_LENGTH(LPAD('😀', 524288, '😀')), CHAR_LENGTH(RPAD('😀', 524288, '😀')), CHAR_LENGTH(REPLACE(LPAD('a', 1024, 'a'), 'a', LPAD('b', 1024, 'b'))), CHAR_LENGTH(CONCAT(LPAD('a', 524288), LPAD('b', 524288))), LPAD('x', 2147483647, ''), RPAD('x', 2147483647, '') FROM t", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        (await SqlT1ScalarTests.ReadAsync(result)).ShouldBe([[524288L, 524288L, 1048576L, 1048576L, "x", "x"]]);
    }
}
