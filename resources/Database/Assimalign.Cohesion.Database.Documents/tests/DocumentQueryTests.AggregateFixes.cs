using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed partial class DocumentQueryTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - EVERY: quantified predicates carry aggregate permission and group context")]
    [InlineData("SELECT EVERY x IN [TRUE] SATISFIES EVERY(flag) END FROM items")]
    [InlineData("SELECT EVERY x IN [TRUE] SATISFIES EVERY(flag) END FROM items GROUP BY flag")]
    [InlineData("SELECT EVERY(flag) FROM items HAVING EVERY x IN [TRUE] SATISFIES EVERY(flag) END")]
    [InlineData("SELECT EVERY(flag) FROM items ORDER BY EVERY x IN [TRUE] SATISFIES EVERY(flag) END")]
    [InlineData("SELECT EVERY x IN ARRAY_AGG(flag) SATISFIES x AND EVERY(flag) END FROM items")]
    public async Task Execute_QuantifiedAggregateEvery_ShouldUseGroupAsync(string query)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("every-group", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{flag:TRUE}),('b',{flag:TRUE})", cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, query);

        // Assert
        rows.ShouldHaveSingleItem().GetValue(0).ShouldBe(true);
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await RowsAsync(session,
            "SELECT EVERY(EVERY x IN [TRUE] SATISFIES EVERY(flag) END) FROM items"));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05001");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - STDDEV: exact range checks preserve the decimal boundary")]
    [InlineData("[-79228162514264337593543950335,0,79228162514264337593543950335]", "79228162514264337593543950335")]
    [InlineData("[-79228162514264337593543950334,0,79228162514264337593543950334]", "79228162514264337593543950334")]
    [InlineData("[-79228162514264337593543950335,1,79228162514264337593543950335]", null)]
    [InlineData("[-79228162514264337593543950335,79228162514264337593543950335]", null)]
    public async Task Execute_StddevDecimalBoundary_ShouldUseExactRangeAsync(string values, string? expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("stddev-boundary", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{})", cancellationToken: CancellationToken.None);
        string query = "SELECT STDDEV(v) FROM items UNNEST " + values + " v";

        // Act / Assert
        if (expected is not null)
        {
            (await RowsAsync(session, query)).Single().GetValue(0).ShouldBe(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await RowsAsync(session, query));
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD02007");
        }
    }
}
