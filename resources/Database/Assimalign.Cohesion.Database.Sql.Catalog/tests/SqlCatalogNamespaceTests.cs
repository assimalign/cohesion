using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

public sealed class SqlCatalogNamespaceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - SQL namespace records preserve ownership and immutable snapshots across open")]
    public async Task CreateSchemaAsync_EmptyNamespace_ShouldPersistWithoutSeedingOnOpen()
    {
        // Arrange
        using var data = new MemoryStream();
        using var journal = new MemoryStream();
        using var storage = SqlStorage.Create(new NonClosingStream(data), new NonClosingStream(journal), new MemoryStream(), "namespaces");
        var catalog = SqlCatalog.Open(storage);
        catalog.Namespaces.ShouldBeEmpty();
        catalog.DdlGeneration.ShouldBe(0UL);

        // Act
        await catalog.CreateSchemaAsync(new SqlCatalogNamespace("Sales", DatabaseObjectOwner.Schema, "App"), CancellationToken.None);
        var capture = catalog.CaptureSnapshot();
        await catalog.CreateSchemaAsync(new SqlCatalogNamespace("empty"), CancellationToken.None);
        using var reopenedStorage = Reopen(data, journal);
        var reopened = SqlCatalog.Open(reopenedStorage);

        // Assert
        capture.Namespaces.ShouldHaveSingleItem().Name.ShouldBe("Sales");
        reopened.Namespaces.Select(schema => schema.Name).Order().ShouldBe(["empty", "Sales"]);
        var schema = reopened.Namespaces.Single(schema => schema.Name == "Sales");
        schema.Owner.ShouldBe(DatabaseObjectOwner.Schema);
        schema.OwningSchema.ShouldBe("App");
        reopened.DdlGeneration.ShouldBe(2UL);
        await Should.ThrowAsync<SqlCatalogException>(() => reopened.CreateSchemaAsync(new SqlCatalogNamespace("SALES"), CancellationToken.None).AsTask());
        await reopened.DropSchemaAsync("sales", cancellationToken: CancellationToken.None);
        capture.Namespaces.ShouldHaveSingleItem().Name.ShouldBe("Sales");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Namespace CASCADE is one durable transaction, including identity and incoming FK metadata")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DropSchemaAsync_CommitAcceptedOrRefused_ShouldPublishAllOrNone(bool refuse)
    {
        // Arrange
        using var data = new MemoryStream();
        using var journal = new MemoryStream();
        var journalHandle = new CatalogDurabilityHandle(journal);
        using var storage = SqlStorage.Create(new StorageStream(new CatalogDurabilityHandle(data)), new StorageStream(journalHandle),
            new StorageStream(new CatalogDurabilityHandle(new MemoryStream())), "namespace-atomic");
        var catalog = SqlCatalog.Open(storage);
        await catalog.CreateSchemaAsync(new SqlCatalogNamespace("sales"), CancellationToken.None);
        var parent = await catalog.CreateTableAsync("sales", "parent", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), false,
            identity: new SqlCatalogIdentity(true, 1, 1))], ["id"], cancellationToken: CancellationToken.None);
        await catalog.CreateTableAsync("sales", "sibling", [Column()], [], DatabaseObjectOwner.Adhoc, null, CancellationToken.None, constraints:
            [new SqlCatalogConstraint("fk_sibling", SqlCatalogConstraintKind.Reference, ["id"], "sales", "parent", ["id"])]);
        await catalog.CreateTableAsync("dbo", "external", [Column()], [], DatabaseObjectOwner.Adhoc, null, CancellationToken.None, constraints:
            [new SqlCatalogConstraint("fk_external", SqlCatalogConstraintKind.Reference, ["id"], "sales", "parent", ["id"])]);
        catalog.AllocateIdentity(parent).ShouldBe(1L);
        ulong generation = catalog.DdlGeneration;
        journalHandle.SupportsDurableFlush = !refuse;

        // Act
        if (refuse)
        {
            await Should.ThrowAsync<NotSupportedException>(() => catalog.DropSchemaAsync("sales", true, CancellationToken.None).AsTask());
        }
        else { await catalog.DropSchemaAsync("sales", true, CancellationToken.None); }
        journalHandle.SupportsDurableFlush = true;
        using var reopenedStorage = Reopen(data, journal);
        var reopened = SqlCatalog.Open(reopenedStorage);

        // Assert
        foreach (var image in new[] { catalog, reopened })
        {
            image.DdlGeneration.ShouldBe(generation + (refuse ? 0UL : 1UL));
            image.Namespaces.Count.ShouldBe(refuse ? 1 : 0);
            image.Tables.Count.ShouldBe(refuse ? 3 : 1);
            image.Tables.Single(table => table.Name == "external").Constraints.Count.ShouldBe(refuse ? 1 : 0);
            image.GetPendingTableReclamations().Count.ShouldBe(refuse ? 0 : 2);
        }
        if (refuse)
        {
            await catalog.DropSchemaAsync("sales", true, CancellationToken.None);
            catalog.Tables.ShouldHaveSingleItem().Name.ShouldBe("external");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Malformed namespace records are refused")]
    [InlineData("null")]
    [InlineData("{\"Name\":\"\",\"Owner\":0}")]
    [InlineData("{\"Name\":\"s\",\"Owner\":99}")]
    [InlineData("{\"Name\":\"s\",\"Owner\":0,\"unknown\":true}")]
    public void Open_MalformedNamespace_ShouldRefuseRecord(string json)
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "invalid-namespace");
        using (var transaction = storage.BeginTransaction())
        {
            storage.InsertRow(transaction, new DatabaseKeyWriter().AppendInt32(15).AppendString(json, Collation.Binary).ToArray());
            transaction.Commit();
        }

        // Act / Assert
        Should.Throw<SqlCatalogException>(() => SqlCatalog.Open(storage)).Message.ShouldContain("namespace record");
    }

    private static SqlCatalogColumn Column() => new("id", new DatabaseTypeInfo(DatabaseType.Int32), false);

    private static SqlStorage Reopen(MemoryStream data, MemoryStream journal)
    {
        var dataCopy = new MemoryStream();
        dataCopy.Write(data.ToArray());
        dataCopy.Position = 0;
        var journalCopy = new MemoryStream();
        journalCopy.Write(journal.ToArray());
        journalCopy.Position = 0;
        return SqlStorage.Open(dataCopy, journalCopy, new MemoryStream());
    }
}
