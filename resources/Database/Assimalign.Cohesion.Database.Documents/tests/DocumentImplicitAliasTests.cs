using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentImplicitAliasTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Implicit alias: UPDATE targets and values share the source binding")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_QualifiedUpdate_ShouldWriteRootFieldAsync(bool nested)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("implicit-alias", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE COLLECTION items", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {g:1})", cancellationToken: CancellationToken.None);

        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session,
            "UPDATE items SET items.g = items.g + 1" + (nested ? " WHERE EXISTS (SELECT RAW 1 FROM [1] AS s)" : "") + " RETURNING RAW META().id");
        var images = await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW d FROM items d");

        // Assert
        rows.ShouldHaveSingleItem().GetString(0).ShouldBe("a");
        images.ShouldHaveSingleItem().GetValue(0)!.ToString().ShouldBe("{\"g\":2}");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Implicit alias: a self-named-field index preserves nested DELETE results")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_QualifiedDelete_ShouldMatchScanAsync(bool indexed, bool nested)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("implicit-alias", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE COLLECTION items", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {g:1})", cancellationToken: CancellationToken.None);
        if (indexed) { await session.ExecuteAsync("CREATE INDEX ix ON items(items.g)", cancellationToken: CancellationToken.None); }

        // Act
        var result = await session.ExecuteAsync(
            "DELETE FROM items WHERE items.g = 1" + (nested ? " AND EXISTS (SELECT RAW 1 FROM [1] AS s)" : ""), cancellationToken: CancellationToken.None);

        // Assert
        result.AffectedCount.ShouldBe(1);
    }
}
