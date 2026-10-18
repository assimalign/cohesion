using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Catalog.Internal;
using Assimalign.Cohesion.Database.Types;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

public partial class SqlCatalogTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Views: all table publication collision sites protect the shared namespace")]
    public async Task PublishTableAsync_ViewCollision_ShouldRefuseCreateReservePublishAndRename()
    {
        // Arrange
        var catalog = new CatalogHarness().Open();
        await catalog.CreateSchemaAsync(new SqlCatalogNamespace("dbo"), CancellationToken.None);
        var source = await catalog.CreateTableAsync("dbo", "source", [Column("id", DatabaseType.Int32)], [], CancellationToken.None);
        var other = await catalog.CreateTableAsync("dbo", "other", [Column("id", DatabaseType.Int32)], [], CancellationToken.None);
        var reserved = await catalog.ReserveTableAsync("dbo", "v", source.Columns, [], [], DatabaseObjectOwner.Adhoc, null, CancellationToken.None);
        await catalog.CreateViewAsync(new SqlCatalogView("dbo", "v", "SELECT id FROM source", source.Columns,
            [source.ObjectId], [SqlObjectIdentity.Column(source.ObjectId, "id")], []), cancellationToken: CancellationToken.None);
        ulong before = catalog.DdlGeneration;
        // Act / Assert
        await Should.ThrowAsync<SqlCatalogException>(() => catalog.CreateTableAsync("dbo", "v", source.Columns, [], CancellationToken.None).AsTask());
        await Should.ThrowAsync<SqlCatalogException>(() => catalog.ReserveTableAsync("dbo", "v", source.Columns, [], [], DatabaseObjectOwner.Adhoc, null, CancellationToken.None).AsTask());
        var publication = await Should.ThrowAsync<SqlCatalogException>(() => catalog.PublishTableAsync(reserved, [], [], cancellationToken: CancellationToken.None).AsTask());
        await Should.ThrowAsync<SqlCatalogException>(() => catalog.RenameTableAsync(other, "v", new Dictionary<string, string>(), CancellationToken.None).AsTask());
        publication.Message.ShouldContain("dbo.v", Case.Sensitive);
        catalog.DdlGeneration.ShouldBe(before);
        catalog.Views.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Views: immutable records round-trip with namespace type modifiers and collation")]
    public async Task CreateViewAsync_Record_ShouldPersistFrozenContractAndSnapshot()
    {
        // Arrange
        var harness = new CatalogHarness();
        var catalog = harness.Open();
        await catalog.CreateSchemaAsync(new SqlCatalogNamespace("dbo"), CancellationToken.None);
        var table = await catalog.CreateTableAsync("dbo", "t", [Column("id", DatabaseType.Int32)], [], CancellationToken.None);
        var columns = new List<SqlCatalogColumn> { new("label", new DatabaseTypeInfo(DatabaseType.String, 12), collation: Collation.CaseInsensitive) };
        var view = new SqlCatalogView("dbo", "v", "SELECT CAST(id AS VARCHAR(12)) COLLATE case_insensitive AS label FROM t", columns,
            [table.ObjectId], [SqlObjectIdentity.Column(table.ObjectId, "id")], []);
        // Act
        await catalog.CreateViewAsync(view, cancellationToken: CancellationToken.None);
        var snapshot = catalog.CaptureSnapshot();
        columns.Clear();
        var reopened = harness.Reopen();
        await catalog.DropViewAsync("dbo", "v", cancellationToken: CancellationToken.None);
        // Assert
        snapshot.Views.ShouldHaveSingleItem().Columns.ShouldHaveSingleItem().Name.ShouldBe("label");
        var restored = reopened.Views.ShouldHaveSingleItem();
        restored.Definition.ShouldBe(view.Definition);
        restored.Columns[0].Type.MaxLength.ShouldBe(12);
        restored.Columns[0].Collation.ShouldBeSameAs(Collation.CaseInsensitive);
        restored.TableDependencies.ShouldBe([table.ObjectId]);
        catalog.Views.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Views: publication guards names columns table identity and view dependencies")]
    public async Task Mutate_ViewDependency_ShouldRefuseBeforeCatalogPublication()
    {
        // Arrange
        var catalog = new CatalogHarness().Open();
        await catalog.CreateSchemaAsync(new SqlCatalogNamespace("dbo"), CancellationToken.None);
        var table = await catalog.CreateTableAsync("dbo", "t", [Column("id", DatabaseType.Int32), Column("unused", DatabaseType.Int32)], [], CancellationToken.None);
        var view = new SqlCatalogView("dbo", "v", "SELECT id FROM t", [Column("id", DatabaseType.Int32)],
            [table.ObjectId], [SqlObjectIdentity.Column(table.ObjectId, "id")], []);
        await catalog.CreateViewAsync(view, cancellationToken: CancellationToken.None);
        ulong before = catalog.DdlGeneration;
        // Act / Assert
        (await Should.ThrowAsync<SqlCatalogException>(() => catalog.DropTableAsync("dbo", "t", CancellationToken.None).AsTask())).Message.ShouldContain("dbo.v");
        await Should.ThrowAsync<SqlCatalogException>(() => catalog.DropColumnAsync("dbo", "t", "id", CancellationToken.None).AsTask());
        await Should.ThrowAsync<SqlCatalogException>(() => catalog.RenameTableAsync(table, "renamed", new Dictionary<string, string>(), CancellationToken.None).AsTask());
        await Should.ThrowAsync<SqlCatalogException>(() => catalog.RenameColumnAsync(table, "id", "renamed", new Dictionary<string, string>(), CancellationToken.None).AsTask());
        await Should.ThrowAsync<SqlCatalogException>(() => catalog.CreateTableAsync("dbo", "v", [Column("id", DatabaseType.Int32)], [], CancellationToken.None).AsTask());
        catalog.DdlGeneration.ShouldBe(before);
        await catalog.DropColumnAsync("dbo", "t", "unused", CancellationToken.None);
        await catalog.CreateViewAsync(new SqlCatalogView("dbo", "w", "SELECT id FROM v", view.Columns, view.TableDependencies,
            [SqlObjectIdentity.View("dbo", "v")], []), cancellationToken: CancellationToken.None);
        await Should.ThrowAsync<SqlCatalogException>(() => catalog.DropViewAsync("dbo", "v", cancellationToken: CancellationToken.None).AsTask());
        await catalog.DropViewAsync("dbo", "v", cascade: true, cancellationToken: CancellationToken.None);
        catalog.Views.ShouldBeEmpty();
    }
}
