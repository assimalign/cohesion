using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

public sealed class SqlCatalogScriptTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - A publication refuses inconsistent script identity atomically")]
    [InlineData("checksum")]
    [InlineData("count")]
    [InlineData("origin")]
    public async Task CreateTableAsync_InconsistentReceipt_ShouldRefusePublication(string changed)
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-identity");
        var catalog = SqlCatalog.Open(storage);
        ulong generation = catalog.DdlGeneration;
        var original = new SqlCatalogScript("001", new string('A', 64), 1, 0, generation, generation, null);
        await catalog.SaveScriptAsync(original, CancellationToken.None);
        var invalid = new SqlCatalogScript("001", new string(changed == "checksum" ? 'B' : 'A', 64), changed == "count" ? 2 : 1,
            1, changed == "origin" ? generation + 1 : generation, generation + 1, null);

        // Act
        var failure = await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.CreateTableAsync("dbo", "t",
            [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)], cancellationToken: CancellationToken.None, scriptReceipt: invalid));

        // Assert
        failure.Message.ShouldContain("COHSQLS001");
        catalog.Tables.ShouldBeEmpty();
        catalog.Scripts.ShouldHaveSingleItem().ShouldBe(original);
        catalog.DdlGeneration.ShouldBe(generation);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Reservation records progress only when its table publishes")]
    public async Task ReserveTableAsync_GivenReceipt_ShouldDeferCompletionUntilPublication()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-reservation");
        var catalog = SqlCatalog.Open(storage);
        var receipt = new SqlCatalogScript("001", new string('A', 64), 1, 1, catalog.DdlGeneration, catalog.DdlGeneration, null, ["T:dbo.t"]);

        // Act
        var table = await catalog.ReserveTableAsync("dbo", "t", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)],
            null, [], DatabaseObjectOwner.Schema, "App", CancellationToken.None, receipt);

        // Assert: the reservation is durable but the statement has not published.
        catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(0);
        catalog.Scripts.ShouldHaveSingleItem().Objects.ShouldBeEmpty();
        catalog.Tables.ShouldBeEmpty();
        await catalog.CreateTableAsync("dbo", "unrelated", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)], cancellationToken: CancellationToken.None);
        catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(0);
        await catalog.PublishTableAsync(table, [], [], cancellationToken: CancellationToken.None, scriptReceipt: receipt);
        catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(1);
        catalog.Scripts.ShouldHaveSingleItem().Generation.ShouldBe(catalog.DdlGeneration);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - A public table publication completes only its given script receipt")]
    public async Task CreateTableAsync_GivenReceipt_ShouldCompleteOnlyThatPublication()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-receipt");
        var catalog = SqlCatalog.Open(storage);
        var receipt = new SqlCatalogScript("001", new string('A', 64), 1, 1, catalog.DdlGeneration, catalog.DdlGeneration, null, ["T:dbo.t"]);

        // Act
        await catalog.CreateTableAsync("dbo", "t", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)], cancellationToken: CancellationToken.None, scriptReceipt: receipt);
        ulong publication = catalog.DdlGeneration;
        await catalog.CreateTableAsync("dbo", "unrelated", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)], cancellationToken: CancellationToken.None);

        // Assert
        catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(1);
        catalog.Scripts.ShouldHaveSingleItem().Generation.ShouldBe(publication);
        catalog.Scripts.ShouldHaveSingleItem().Objects.ShouldBe(["T:dbo.t"]);
        catalog.Tables.Count.ShouldBe(2);
        await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.CreateTableAsync("dbo", "reused_receipt",
            [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)], cancellationToken: CancellationToken.None, scriptReceipt: receipt));
        catalog.Tables.Count.ShouldBe(2);
        catalog.Scripts.ShouldHaveSingleItem().Generation.ShouldBe(publication);
    }
}
