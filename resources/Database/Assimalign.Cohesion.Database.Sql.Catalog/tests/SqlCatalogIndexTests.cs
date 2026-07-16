using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

/// <summary>
/// Tests for secondary-index metadata in the catalog (#912): index descriptions
/// persist atomically with their physical registrations, survive reopen, guard
/// dependent-column drops, and fall with their table.
/// </summary>
public class SqlCatalogIndexTests
{
    private static SqlCatalogColumn Column(string name, DatabaseType type, bool nullable = true)
        => new(name, new DatabaseTypeInfo(type), nullable);

    private sealed class CatalogHarness
    {
        private readonly MemoryStream _data = new();
        private readonly MemoryStream _journal = new();

        public ISqlCatalog Open()
        {
            var storage = SqlStorage.Create(
                new NonClosingStream(_data), new NonClosingStream(_journal), new MemoryStream(), "index-catalog-test");
            return SqlCatalog.Open(storage);
        }

        public ISqlCatalog Reopen()
        {
            var dataCopy = new MemoryStream();
            dataCopy.Write(_data.ToArray());
            var journalCopy = new MemoryStream();
            journalCopy.Write(_journal.ToArray());

            var storage = SqlStorage.Open(dataCopy, journalCopy, new MemoryStream());
            return SqlCatalog.Open(storage);
        }
    }

    private static async Task<(ISqlCatalog Catalog, CatalogHarness Harness, SqlCatalogTable Table)> OpenWithTableAsync()
    {
        var harness = new CatalogHarness();
        var catalog = harness.Open();
        var table = await catalog.CreateTableAsync("dbo", "users", new[]
        {
            Column("id", DatabaseType.Int32, nullable: false),
            Column("email", DatabaseType.String),
            Column("age", DatabaseType.Int32),
        });

        return (catalog, harness, table);
    }

    private static BTreeIndexRegistration Registration(ulong objectId, string name, bool unique, long rootPageId)
        => new(objectId, new IndexDefinition(name, IndexKind.BTree, unique), rootPageId);

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Indexes: description and registration persist atomically and reload on reopen")]
    public async Task CreateIndex_WithRegistrations_ShouldPersistAcrossReopen()
    {
        // Arrange
        var (catalog, harness, table) = await OpenWithTableAsync();
        var registration = Registration(table.ObjectId, "ix_email", unique: true, rootPageId: 42);

        // Act
        await catalog.CreateIndexAsync(
            new SqlCatalogIndex(table.ObjectId, "ix_email", new[] { "email" }, isUnique: true),
            new[] { registration });

        // Assert: live lookup...
        catalog.TryGetIndex(table.ObjectId, "IX_EMAIL", out var live).ShouldBeTrue(); // case-insensitive
        live.IsUnique.ShouldBeTrue();
        live.ColumnNames.ShouldBe(new[] { "email" });

        // ...and the reopened catalog carries both records.
        var reopened = harness.Reopen();
        reopened.TryGetIndex(table.ObjectId, "ix_email", out var persisted).ShouldBeTrue();
        persisted.ColumnNames.ShouldBe(new[] { "email" });
        reopened.GetIndexRegistrations().ShouldBe(new[] { registration });
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Indexes: duplicate names, unknown tables, and unknown columns are rejected")]
    public async Task CreateIndex_InvalidDefinitions_ShouldThrow()
    {
        // Arrange
        var (catalog, _, table) = await OpenWithTableAsync();
        await catalog.CreateIndexAsync(
            new SqlCatalogIndex(table.ObjectId, "ix_email", new[] { "email" }, isUnique: false),
            Array.Empty<BTreeIndexRegistration>());

        // Act + Assert
        await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.CreateIndexAsync(
            new SqlCatalogIndex(table.ObjectId, "IX_EMAIL", new[] { "email" }, isUnique: false),
            Array.Empty<BTreeIndexRegistration>()));

        await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.CreateIndexAsync(
            new SqlCatalogIndex(9999, "ix_ghost", new[] { "email" }, isUnique: false),
            Array.Empty<BTreeIndexRegistration>()));

        await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.CreateIndexAsync(
            new SqlCatalogIndex(table.ObjectId, "ix_missing", new[] { "nope" }, isUnique: false),
            Array.Empty<BTreeIndexRegistration>()));
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Indexes: dropping an indexed column is rejected until the index is dropped")]
    public async Task DropColumn_OnIndexedColumn_ShouldBeRejected()
    {
        // Arrange
        var (catalog, _, table) = await OpenWithTableAsync();
        await catalog.CreateIndexAsync(
            new SqlCatalogIndex(table.ObjectId, "ix_email", new[] { "email" }, isUnique: false),
            Array.Empty<BTreeIndexRegistration>());

        // Act + Assert: guarded while the index exists...
        var exception = await Should.ThrowAsync<SqlCatalogException>(
            async () => await catalog.DropColumnAsync("dbo", "users", "email"));
        exception.Message.ShouldContain("ix_email");

        // ...and allowed after it is dropped.
        await catalog.DropIndexAsync(table.ObjectId, "ix_email", Array.Empty<BTreeIndexRegistration>());
        var updated = await catalog.DropColumnAsync("dbo", "users", "email");
        updated.FindColumn("email").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Indexes: DROP TABLE removes its index descriptions and registrations")]
    public async Task DropTable_WithIndexes_ShouldRemoveIndexMetadata()
    {
        // Arrange: two tables; only one is dropped.
        var (catalog, harness, table) = await OpenWithTableAsync();
        var other = await catalog.CreateTableAsync("dbo", "orders", new[] { Column("id", DatabaseType.Int32, nullable: false) });

        await catalog.CreateIndexAsync(
            new SqlCatalogIndex(table.ObjectId, "ix_email", new[] { "email" }, isUnique: false),
            new[] { Registration(table.ObjectId, "ix_email", false, 7), Registration(other.ObjectId, "ix_orders", false, 9) });
        await catalog.CreateIndexAsync(
            new SqlCatalogIndex(other.ObjectId, "ix_orders", new[] { "id" }, isUnique: false),
            new[] { Registration(table.ObjectId, "ix_email", false, 7), Registration(other.ObjectId, "ix_orders", false, 9) });

        // Act
        await catalog.DropTableAsync("dbo", "users");

        // Assert: the dropped table's index metadata and registration are gone,
        // the survivor's remain — including after reopen.
        catalog.GetIndexes(table.ObjectId).ShouldBeEmpty();
        catalog.GetIndexes(other.ObjectId).Count.ShouldBe(1);
        catalog.GetIndexRegistrations().Single().ObjectId.ShouldBe(other.ObjectId);

        var reopened = harness.Reopen();
        reopened.GetIndexes(table.ObjectId).ShouldBeEmpty();
        reopened.GetIndexes(other.ObjectId).Count.ShouldBe(1);
        reopened.GetIndexRegistrations().Single().ObjectId.ShouldBe(other.ObjectId);
    }
}
