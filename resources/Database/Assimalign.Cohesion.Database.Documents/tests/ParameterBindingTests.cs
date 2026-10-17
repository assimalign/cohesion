using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Documents.Language;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class ParameterBindingTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Parameters: a malformed typed request keeps its parse diagnostic")]
    public async Task Execute_MalformedTypedParameter_ShouldRejectParsingBeforeBindingAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("parameters", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse("SELECT $1a FROM t");

        // Act
        var failure = await Should.ThrowAsync<DatabaseParseException>(async () =>
            await session.ExecuteAsync(new DocumentQueryRequest(statement), CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("OQL0002");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Parameters: both sigils bind bare and sigiled ordinal keys")]
    [InlineData("$id", "id")]
    [InlineData("@id", "id")]
    [InlineData("$id", "@id")]
    [InlineData("@id", "$id")]
    [InlineData("$1", "1")]
    [InlineData("@1", "$1")]
    [InlineData("$01", "01")]
    [InlineData("$_x", "_x")]
    public async Task Execute_ParameterSpelling_ShouldBindSameValueAsync(string reference, string key)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("parameters", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("t", CancellationToken.None);
        await collection.PutAsync(session, "one", "{\"a\":1,\"b\":1}"u8.ToArray(), cancellationToken: CancellationToken.None);
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [key] = 7, ["unused"] = new object() };
        var request = DocumentQueryRequest.FromOql($"SELECT {reference} AS value FROM t", parameters);

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

    [Fact(DisplayName = "Cohesion Test [Documents] - Parameters: a supplied null is bound")]
    public async Task Execute_NullParameter_ShouldReturnNullAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("parameters", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("t", CancellationToken.None);
        await collection.PutAsync(session, "one", "{}"u8.ToArray(), cancellationToken: CancellationToken.None);

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

    [Theory(DisplayName = "Cohesion Test [Documents] - Parameters: normalized key collisions and case mismatches are coded")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_AmbiguousOrWrongCaseKeys_ShouldRejectAsync(bool collision)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("parameters", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("t", CancellationToken.None);
        var parameters = collision
            ? new Dictionary<string, object?> { ["id"] = 1, ["@id"] = 2 }
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["ID"] = 1 };

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(collision ? "SELECT a FROM t" : "SELECT $id AS value FROM t", parameters, CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(collision ? "COHDOC009" : "COHDOC005");
        failure.Message.ShouldContain("id");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Parameters: missing values fail before empty input can hide them")]
    [InlineData("SELECT a FROM t WHERE b = $missing")]
    [InlineData("SELECT $missing AS value FROM t")]
    [InlineData("SELECT a FROM t ORDER BY $missing")]
    [InlineData("SELECT COUNT(*) AS total FROM t HAVING COUNT(*) > $missing")]
    public async Task Execute_UnboundParameterOverEmptyInput_ShouldFailBeforeRowsAsync(string query)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("parameters", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("t", CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDOC005");
        failure.Message.ShouldContain("missing");
        diagnostic.Start.ShouldBe(query.IndexOf("missing", StringComparison.Ordinal) - 1);
        diagnostic.End.ShouldBe(diagnostic.Start + 8);
    }
}
