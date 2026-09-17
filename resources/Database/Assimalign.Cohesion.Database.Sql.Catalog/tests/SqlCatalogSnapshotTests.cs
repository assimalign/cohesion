using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

/// <summary>Verifies the complete catalog images used by SQL system-view statements.</summary>
public sealed class SqlCatalogSnapshotTests
{
    /// <summary>Captures before, during, and after DDL retain complete table and index definitions.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Snapshot: publication and drop preserve complete catalog images")]
    public async Task CaptureSnapshot_AcrossPublicationAndDrop_ShouldRetainWholeCatalogImages()
    {
        // Arrange: a CREATE has reserved an identity but has not published its
        // table and enforcing index. A metadata reader must not see that draft.
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "snapshot");
        var catalog = SqlCatalog.Open(storage);
        var check = new SqlCatalogConstraint("ck_id", SqlCatalogConstraintKind.Check, ["id"], checkExpression: "id > 0");
        var table = await SqlCatalog.ReserveTableAsync(catalog, "dbo", "orders",
            [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), false)],
            ["id"], [check], DatabaseObjectOwner.Adhoc, null, CancellationToken.None);
        var beforePublish = SqlCatalog.CaptureSnapshot(catalog);
        var index = new SqlCatalogIndex(table.ObjectId, "pk_orders", ["id"], true, isPrimaryKey: true);
        BTreeIndexRegistration[] registrations =
            [new(table.ObjectId, new IndexDefinition(index.Name, IndexKind.BTree, true), 7)];

        // Act: keep a read image while the live catalog publishes then drops
        // both definitions. Subsequent lookups must use the captured directory.
        await SqlCatalog.PublishTableAsync(catalog, table, [index], registrations, CancellationToken.None);
        var afterPublish = SqlCatalog.CaptureSnapshot(catalog);
        await catalog.DropTableAsync("dbo", "orders", CancellationToken.None);
        var afterDrop = SqlCatalog.CaptureSnapshot(catalog);

        // Assert
        beforePublish.Tables.ShouldBeEmpty();
        beforePublish.GetIndexes(table.ObjectId).ShouldBeEmpty();
        afterPublish.TryGetTable("DBO", "ORDERS", out var captured).ShouldBeTrue();
        captured.PrimaryKeyColumns.ShouldBe(["id"]);
        captured.Constraints.ShouldHaveSingleItem().CheckExpression.ShouldBe("id > 0");
        afterPublish.GetIndexes(captured.ObjectId).ShouldHaveSingleItem().Name.ShouldBe("pk_orders");
        afterPublish.Tables.ShouldHaveSingleItem().ObjectId.ShouldBe(captured.ObjectId);
        afterDrop.Tables.ShouldBeEmpty();
        afterDrop.TryGetTable("dbo", "orders", out _).ShouldBeFalse();
        afterDrop.GetIndexes(table.ObjectId).ShouldBeEmpty();
    }
}
