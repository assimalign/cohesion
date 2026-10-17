using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class ParameterBindingTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Parameters: Unicode numeric names never bind matching map keys")]
    [InlineData("$\u0661")]
    [InlineData("@\u0661")]
    [InlineData("$1\u0661")]
    public async Task Execute_UnicodeNumericParameter_ShouldRejectBeforeBindingAsync(string reference)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("parameters", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (a INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);
        string query = $"SELECT {reference} AS value FROM t;";
        var parameters = new Dictionary<string, object?> { [reference[1..]] = 7 };

        // Act
        var failure = await Should.ThrowAsync<DatabaseParseException>(async () =>
            await session.ExecuteAsync(query, parameters, CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("SQL0003");
        diagnostic.Start.ShouldBe(7);
        diagnostic.End.ShouldBe(7 + reference.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Parameters: control requests reject collisions without changing transaction state")]
    [InlineData("BEGIN")]
    [InlineData("COMMIT")]
    [InlineData("ROLLBACK")]
    public async Task Execute_ControlParameterCollision_ShouldPreserveTransactionStateAsync(string command)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("parameters", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        if (command != "BEGIN")
        {
            await session.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None);
        }
        var previous = session.CurrentTransaction;
        var parameters = new Dictionary<string, object?> { ["id"] = 1, ["@id"] = 2 };

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(command, parameters, CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE023");
        failure.Message.ShouldContain("id");
        session.CurrentTransaction.ShouldBe(previous);
        if (previous is not null)
        {
            await session.ExecuteAsync("ROLLBACK", cancellationToken: CancellationToken.None);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Parameters: both sigils bind bare and sigiled ordinal keys")]
    [InlineData("$id", "id")]
    [InlineData("@id", "id")]
    [InlineData("$id", "@id")]
    [InlineData("@id", "$id")]
    [InlineData("$1", "1")]
    [InlineData("@1", "$1")]
    [InlineData("$01", "01")]
    [InlineData("$0", "0")]
    [InlineData("$9223372036854775808", "9223372036854775808")]
    [InlineData("$_x", "_x")]
    public async Task Execute_ParameterSpelling_ShouldBindSameValueAsync(string reference, string key)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("parameters", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (a INT, b INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1, 1)", cancellationToken: CancellationToken.None);
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [key] = 7, ["unused"] = new object() };
        var request = SqlQueryRequest.FromSql($"SELECT {reference} AS value FROM t", parameters);

        // Act / Assert: a reusable typed request snapshots again for each execution.
        for (int expected = 7; expected <= 8; expected++)
        {
            parameters[key] = expected;
            var result = (DatabaseResultSet)await session.ExecuteAsync(request, CancellationToken.None);
            await using (result)
            {
                int count = 0;
                await foreach (var row in result.GetRowsAsync(CancellationToken.None))
                {
                    row.GetInt32(0).ShouldBe(expected);
                    count++;
                }
                count.ShouldBe(1);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Parameters: grouping compares bare names")]
    public async Task Execute_MixedSigilGroupKey_ShouldReturnOneGroupAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("parameters", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (a INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1), (2)", cancellationToken: CancellationToken.None);

        // Act
        var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT @a, COUNT(*) FROM t GROUP BY $a",
            new Dictionary<string, object?> { ["a"] = 9 }, CancellationToken.None);

        // Assert
        await using (result)
        {
            int count = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            {
                row.GetInt32(0).ShouldBe(9);
                row.GetInt64(1).ShouldBe(2);
                count++;
            }
            count.ShouldBe(1);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Parameters: a supplied null is bound")]
    public async Task Execute_NullParameter_ShouldReturnNullAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("parameters", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (a INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);

        // Act
        var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT $id AS value FROM t",
            new Dictionary<string, object?> { ["@id"] = null }, CancellationToken.None);

        // Assert
        await using (result)
        {
            int count = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            {
                row.IsNull(0).ShouldBeTrue();
                count++;
            }
            count.ShouldBe(1);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Parameters: normalized key collisions and case mismatches are coded")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_AmbiguousOrWrongCaseKeys_ShouldRejectAsync(bool collision)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("parameters", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (a INT, b INT)", cancellationToken: CancellationToken.None);
        var parameters = collision
            ? new Dictionary<string, object?> { ["id"] = 1, ["@id"] = 2 }
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["ID"] = 1 };

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(collision ? "SELECT a FROM t" : "SELECT $id AS value FROM t", parameters, CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(collision ? "COHSQLE023" : "COHSQLE020");
        failure.Message.ShouldContain("id");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Parameters: missing values fail before empty input can hide them")]
    [InlineData("SELECT a FROM t WHERE b = $missing")]
    [InlineData("SELECT $missing AS value FROM t")]
    [InlineData("SELECT a FROM t ORDER BY $missing")]
    [InlineData("SELECT COUNT(*) AS total FROM t HAVING COUNT(*) > $missing")]
    [InlineData("DELETE FROM t WHERE b = @missing")]
    [InlineData("UPDATE t SET a = $missing")]
    [InlineData("SELECT a FROM t WHERE 1 = 0 AND b = $missing")]
    [InlineData("INSERT INTO t VALUES (1, 1), ($missing, 1)")]
    [InlineData("SELECT a FROM t WHERE b IN (SELECT $missing FROM t)")]
    public async Task Execute_UnboundParameterOverEmptyInput_ShouldFailBeforeRowsAsync(string query)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("parameters", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (a INT, b INT)", cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHSQLE020");
        failure.Message.ShouldContain("missing");
        diagnostic.Start.ShouldBe(query.IndexOf("missing", StringComparison.Ordinal) - 1);
        diagnostic.End.ShouldBe(diagnostic.Start + 8);
    }
}
