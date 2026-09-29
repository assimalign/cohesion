using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

/// <summary>Verifies the complete catalog images used by SQL system-view statements.</summary>
public sealed class SqlCatalogSnapshotTests
{
    /// <summary>Publication cannot bypass catalog identity allocation and introduce duplicate future identities.</summary>
    /// <param name="objectId">An identity that the catalog has not reserved.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Publication: unreserved identities are rejected")]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(42UL)]
    public async Task PublishTableAsync_WithUnreservedIdentity_ShouldRejectDefinition(ulong objectId)
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "unreserved");
        var catalog = SqlCatalog.Open(storage);
        SqlCatalogColumn[] columns = [new("id", new DatabaseTypeInfo(DatabaseType.Int32), false)];
        var unreserved = new SqlCatalogTable(objectId, "dbo", "unreserved", columns);

        // Act
        await Should.ThrowAsync<SqlCatalogException>(() => SqlCatalog.PublishTableAsync(catalog,
            unreserved, [], [], cancellationToken: CancellationToken.None).AsTask());

        // Assert: failure publishes nothing and leaves the next legitimate identity available.
        SqlCatalog.CaptureSnapshot(catalog).Tables.ShouldBeEmpty();
        var created = await catalog.CreateTableAsync("dbo", "reserved", columns, cancellationToken: CancellationToken.None);
        created.ObjectId.ShouldBe(1UL);
    }

    /// <summary>Caller-owned lists cannot change published or captured catalog metadata.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Snapshot: input mutation cannot change captured metadata")]
    public async Task CaptureSnapshot_AfterInputMutation_ShouldPreserveMetadata()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "immutable-snapshot");
        var catalog = SqlCatalog.Open(storage);
        var originalColumn = new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), false);
        var columns = new List<SqlCatalogColumn> { originalColumn };
        var primaryKeyColumns = new List<string> { "id" };
        var indexColumns = new List<string> { "id" };
        var table = await SqlCatalog.ReserveTableAsync(catalog, "dbo", "orders", columns, primaryKeyColumns, [],
            DatabaseObjectOwner.Adhoc, null, CancellationToken.None);
        var index = new SqlCatalogIndex(table.ObjectId, "pk_orders", indexColumns, true, isPrimaryKey: true);
        await SqlCatalog.PublishTableAsync(catalog, table, [index],
            [new(table.ObjectId, new IndexDefinition(index.Name, IndexKind.BTree, true), 7)],
            cancellationToken: CancellationToken.None);
        var snapshot = SqlCatalog.CaptureSnapshot(catalog);

        // Act
        var replacementColumn = new SqlCatalogColumn("changed", new DatabaseTypeInfo(DatabaseType.String));
        columns[0] = replacementColumn;
        primaryKeyColumns[0] = "changed";
        indexColumns[0] = "changed";

        // Assert: input mutation affects neither a retained capture nor the live catalog.
        var capturedTable = snapshot.Tables.ShouldHaveSingleItem();
        capturedTable.Columns.ShouldHaveSingleItem().ShouldBeSameAs(originalColumn);
        capturedTable.PrimaryKeyColumns.ShouldBe(["id"]);
        var capturedIndex = snapshot.GetIndexes(table.ObjectId).ShouldHaveSingleItem();
        capturedIndex.ColumnNames.ShouldBe(["id"]);
        catalog.Tables.ShouldHaveSingleItem().Columns.ShouldHaveSingleItem().ShouldBeSameAs(originalColumn);
        catalog.Tables.ShouldHaveSingleItem().PrimaryKeyColumns.ShouldBe(["id"]);
        catalog.GetIndexes(table.ObjectId).ShouldHaveSingleItem().ColumnNames.ShouldBe(["id"]);
        Should.Throw<NotSupportedException>(() => ((IList<SqlCatalogColumn>)capturedTable.Columns)[0] = replacementColumn);
        Should.Throw<NotSupportedException>(() => ((IList<string>)capturedTable.PrimaryKeyColumns)[0] = "changed");
        Should.Throw<NotSupportedException>(() => ((IList<string>)capturedIndex.ColumnNames)[0] = "changed");
    }

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
        await SqlCatalog.PublishTableAsync(catalog, table, [index], registrations, cancellationToken: CancellationToken.None);
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
