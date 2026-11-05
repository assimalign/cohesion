using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentCompoundIntegrationTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Compound operands retain implicit aliases and subquery correlation (#1767, #1803)")]
    [InlineData("SELECT RAW items.g FROM items UNION ALL SELECT RAW items.items.g FROM items", "1;2;9;8")]
    [InlineData("SELECT RAW (SELECT RAW items.g FROM [1] AS s) FROM items UNION ALL SELECT RAW (SELECT RAW items.items.g FROM [1] AS s) FROM items", "[1];[2];[9];[8]")]
    [InlineData("SELECT RAW (SELECT RAW i.g FROM items i ORDER BY i.g) FROM items UNION ALL SELECT RAW (SELECT RAW i.g FROM items i ORDER BY i.g) FROM items AS items", "[1,2];[1,2];[1];[2]")]
    [InlineData("SELECT RAW items.g FROM items UNION SELECT RAW items.g FROM items UNION ALL SELECT RAW (SELECT RAW items.items.g FROM [1] AS s) FROM items", "1;2;[9];[8]")]
    public async Task ExecuteAsync_CompoundOperands_ShouldPreserveAliasBindings(string query, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("compound-bindings", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, query);

        // Assert
        string.Join(';', rows.Select(row => row.GetValue(0)?.ToString())).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Array index paths keep CD03027 while compound operands can subscript named variables (#1767, #1803)")]
    [InlineData("DISTINCT ARRAY v[0] FOR v IN tags END")]
    [InlineData("DISTINCT ARRAY v[0].price FOR v IN variants END")]
    [InlineData("DISTINCT ARRAY v FOR v IN tags WHEN v[0] = 10 END")]
    public async Task ExecuteAsync_ArrayIndexElementPath_ShouldRefuseBeforeCompoundQuery(string key)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("compound-array-paths", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "CREATE INDEX invalid ON items(" + key + ")", cancellationToken: CancellationToken.None));

        // Assert: the index grammar requires a property-rooted relative path. Query paths
        // retain their numeric subscripts in every compound operand.
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD03027");
        error.Diagnostics[0].Start.ShouldNotBeNull();
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT * FROM COHESION_SCHEMA.INDEXES")).ShouldBeEmpty();
        var rows = await DocumentSubqueryTests.RowsAsync(session,
            "SELECT RAW v[0] FROM items UNNEST items.tags v UNION ALL SELECT RAW v[0].price FROM items UNNEST items.variants v");
        string.Join(';', rows.Select(row => row.GetValue(0)?.ToString())).ShouldBe("10;20;30;40");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Destination capture keeps CD01036 with compound support (#1767, #1803)")]
    [InlineData("INSERT")]
    [InlineData("UPSERT")]
    public async Task ExecuteAsync_ValuesDestinationCapture_ShouldRefuseWithoutWrites(string verb)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("compound-destination", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            verb + " INTO items (KEY, VALUE) VALUES ('new', {v:(SELECT RAW items.g FROM [1] AS s)})",
            cancellationToken: CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD01036");
        error.Diagnostics[0].Start.ShouldNotBeNull();
        session.LastQuerySubqueriesExecuted.ShouldBe(0);
        var rows = await DocumentSubqueryTests.RowsAsync(session,
            "SELECT RAW items.g FROM items UNION ALL SELECT RAW items.items.g FROM items");
        string.Join(';', rows.Select(row => row.GetValue(0)?.ToString())).ShouldBe("1;2;9;8");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Nested compounds execute and cache independent collection operands (#1803)")]
    public async Task ExecuteAsync_NestedCompound_ShouldCacheIndependentOperands()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("compound-boundary", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session,
            "SELECT RAW (SELECT RAW items.g FROM items UNION ALL SELECT RAW items.g FROM items) FROM items");
        // Assert
        rows.Select(row => row.GetValue(0)?.ToString()).ShouldBe(new[] { "[1,2,1,2]", "[1,2,1,2]" });
        session.LastQuerySubqueriesExecuted.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - DML compounds retain duplicate-key and destination-capture validation (#1803)")]
    [InlineData("INSERT INTO items (KEY id, VALUE doc) SELECT 'new' AS id, {g:3} AS doc FROM items UNION ALL SELECT 'other' AS id, {g:4} AS doc FROM items", "CD03012")]
    [InlineData("INSERT INTO items (KEY, VALUE) VALUES ('new', {v:(SELECT RAW items.g FROM [1] AS s UNION ALL SELECT RAW items.g FROM [1] AS t)})", "CD01036")]
    public async Task ExecuteAsync_InvalidCompoundWrite_ShouldRefuseBeforeMutation(string query, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("compound-boundary", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        session.LastQuerySubqueriesExecuted.ShouldBe(0);
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW META().id FROM items ORDER BY META().id"))
            .Select(row => row.GetString(0)).ShouldBe(["a", "b"]);
    }

    private static async Task SeedAsync(DocumentDatabaseSession session)
    {
        await session.ExecuteAsync("CREATE COLLECTION items", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES " +
            "('a',{g:1,items:{g:9},tags:[[10]],variants:[[{price:30}]]})," +
            "('b',{g:2,items:{g:8},tags:[[20]],variants:[[{price:40}]]})", cancellationToken: CancellationToken.None);
    }
}
