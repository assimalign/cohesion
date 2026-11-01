using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentSourceScopingTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Source scope: own paths distinguish collection fields from explicit aliases")]
    [InlineData("SELECT items.g FROM items GROUP BY g", "CD05001", null)]
    [InlineData("SELECT RAW (SELECT RAW items.g FROM [1] AS s) FROM items GROUP BY items.g", "CD05001", null)]
    [InlineData("SELECT DISTINCT g FROM items ORDER BY items.g", "CD01014", null)]
    [InlineData("SELECT RAW g FROM items ORDER BY items.g", null, "[3,2,1]")]
    [InlineData("SELECT d.g FROM items d GROUP BY g", null, "[1,2,3]")]
    [InlineData("SELECT DISTINCT g FROM items d ORDER BY d.g", null, "[1,2,3]")]
    [InlineData("SELECT RAW g FROM items d ORDER BY d.g", null, "[1,2,3]")]
    public async Task ExecuteAsync_OwnSourcePaths_ShouldPreserveFieldAndAliasScopeAsync(string query, string? code, string? expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("source-scope", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act / Assert
        if (code is not null)
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
            return;
        }
        var rows = await DocumentSubqueryTests.RowsAsync(session, query);
        using var values = JsonDocument.Parse(expected!);
        rows.Select(row => row.GetValue(0)).ShouldBe(values.RootElement.EnumerateArray().Select(value => (object?)value.GetDecimal()));
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - META scope: collection receivers remain equivalent in own grouped and distinct expressions")]
    [InlineData("SELECT RAW META(items).id FROM items GROUP BY META().id ORDER BY META().id")]
    [InlineData("SELECT DISTINCT META().id FROM items ORDER BY META(items).id")]
    public async Task ExecuteAsync_OwnMetadataReceiver_ShouldMatchSourceIdentityAsync(string query)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("metadata-equivalence", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, query);
        // Assert
        rows.Select(row => row.GetValue(0)).ShouldBe(new object?[] { "a", "b", "c" });
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - META scope: only the unaliased collection receiver reaches evaluation")]
    [InlineData("SELECT RAW META(t).id FROM items UNNEST tags AS t")]
    [InlineData("SELECT RAW ARRAY META(v).id FOR v IN tags END FROM items")]
    [InlineData("SELECT RAW META(x).id FROM items")]
    public async Task ExecuteAsync_NonSourceMetadataReceiver_ShouldRefuseDuringPlanningAsync(string query)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("metadata-scope", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05001");
        error.Diagnostics.Single().Start.ShouldNotBeNull();
    }

    private static async Task SeedAsync(DocumentDatabaseSession session)
    {
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES " +
            "('a', {g:3,tags:[1]}), ('b', {g:1,items:{g:30},tags:[2]}), ('c', {g:2,items:{g:10},tags:[3]})",
            cancellationToken: CancellationToken.None);
    }
}
