using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

        public SqlCatalog Open()
        {
            var storage = SqlStorage.Create(
                new NonClosingStream(_data), new NonClosingStream(_journal), new MemoryStream(), "index-catalog-test");
            return SqlCatalog.Open(storage);
        }

        public SqlCatalog Reopen()
        {
            var dataCopy = new MemoryStream();
            dataCopy.Write(_data.ToArray());
            var journalCopy = new MemoryStream();
            journalCopy.Write(_journal.ToArray());

            var storage = SqlStorage.Open(dataCopy, journalCopy, new MemoryStream());
            return SqlCatalog.Open(storage);
        }
    }

    private static async Task<(SqlCatalog Catalog, CatalogHarness Harness, SqlCatalogTable Table)> OpenWithTableAsync()
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

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Directed indexes: orders snapshot and round trip with predicates (#1631)")]
    public async Task CreateIndex_KeyOrders_ShouldCopyAndPersist()
    {
        // Arrange
        var (catalog, harness, table) = await OpenWithTableAsync();
        SqlCatalogIndexKeyOrder[] orders = [SqlCatalogIndexKeyOrder.DescendingNullsLast, SqlCatalogIndexKeyOrder.AscendingNullsFirst];
        var metadata = new SqlCatalogIndex(table.ObjectId, "ix", ["age", "email"], false,
            predicate: "age > 0", predicateColumns: ["age"], keyOrders: orders);
        orders[0] = SqlCatalogIndexKeyOrder.AscendingNullsLast;

        // Act
        await catalog.CreateIndexAsync(metadata, [Registration(table.ObjectId, "ix", false, 42)], CancellationToken.None);
        var recovered = harness.Reopen();

        // Assert
        recovered.TryGetIndex(table.ObjectId, "ix", out var index).ShouldBeTrue();
        index.KeyOrders.ShouldBe([SqlCatalogIndexKeyOrder.DescendingNullsLast, SqlCatalogIndexKeyOrder.AscendingNullsFirst]);
        index.Predicate.ShouldBe("age > 0");
        Should.Throw<SqlCatalogException>(() => new SqlCatalogIndex(1, "bad", ["a"], false,
            keyOrders: [])).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03085");
        Should.Throw<SqlCatalogException>(() => new SqlCatalogIndex(1, "bad", ["a"], false,
            keyOrders: [(SqlCatalogIndexKeyOrder)4])).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03085");
        Should.Throw<SqlCatalogException>(() => new SqlCatalogIndex(1, "bad", ["a"], true, isPrimaryKey: true,
            keyOrders: [SqlCatalogIndexKeyOrder.DescendingNullsFirst])).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03085");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Partial indexes: predicate and immutable dependencies persist symmetrically (#1630)")]
    public async Task CreateIndex_PartialMetadata_ShouldRoundTripAndCopyDependencies()
    {
        // Arrange
        var (catalog, harness, table) = await OpenWithTableAsync();
        string[] columns = ["age"];
        var metadata = new SqlCatalogIndex(table.ObjectId, "ix_adult", ["email"], true,
            predicate: "age > 17", predicateColumns: columns);
        columns[0] = "email";

        // Act
        await catalog.CreateIndexAsync(metadata, [Registration(table.ObjectId, "ix_adult", true, 42)], CancellationToken.None);
        var recovered = harness.Reopen();
        recovered.TryGetIndex(table.ObjectId, "ix_adult", out var index).ShouldBeTrue();

        // Assert
        index.Predicate.ShouldBe("age > 17");
        index.PredicateColumns.ShouldBe(["age"]);
        index.PredicateFunctionCalls.ShouldBeEmpty();
        recovered.GetIndexRegistrations().ShouldHaveSingleItem().Definition.IsUnique.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Partial indexes: malformed or primary predicate metadata is coded (#1630)")]
    public void Constructor_InvalidPartialMetadata_ShouldRefuseWithCodes()
    {
        // Arrange / Act / Assert
        Should.Throw<SqlCatalogException>(() => new SqlCatalogIndex(1, "ix", ["age"], true, isPrimaryKey: true,
            predicate: "age > 0", predicateColumns: ["age"])).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03082");
        Should.Throw<SqlCatalogException>(() => new SqlCatalogIndex(1, "ix", ["age"], false,
            predicateColumns: ["age"])).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03084");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Indexes: duplicate names, unknown tables, and unknown columns are rejected")]
    public async Task CreateIndex_InvalidDefinitions_ShouldThrow()
    {
        // Arrange
        var (catalog, _, table) = await OpenWithTableAsync();
        await catalog.CreateIndexAsync(
            new SqlCatalogIndex(table.ObjectId, "ix_email", new[] { "email" }, isUnique: false),
            [Registration(table.ObjectId, "ix_email", false, 7)]);

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
            [Registration(table.ObjectId, "ix_email", false, 7)]);

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

    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Index publication: invalid registration snapshots leave all metadata unchanged")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PublishIndex_InvalidRegistrationSnapshots_ShouldLeaveCatalogUnchanged(int publication)
    {
        // Arrange: exercise standalone index creation, new-table publication and replacement.
        var (catalog, harness, original) = await OpenWithTableAsync();
        var existing = Registration(original.ObjectId, "ix_age", false, 7);
        await catalog.CreateIndexAsync(new SqlCatalogIndex(original.ObjectId, "ix_age", ["age"], false),
            [existing], CancellationToken.None);
        var table = publication == 1
            ? await catalog.ReserveTableAsync("dbo", "new_users", original.Columns, [], [],
                DatabaseObjectOwner.Adhoc, null, CancellationToken.None)
            : new SqlCatalogTable(original.ObjectId, original.Schema, original.Name,
                [.. original.Columns, Column("added", DatabaseType.Int32)]);
        var index = new SqlCatalogIndex(table.ObjectId, "ix_email", ["email"], true);
        var valid = Registration(table.ObjectId, index.Name, true, 9);
        BTreeIndexRegistration[][] invalidSnapshots =
        [
            [],
            [valid with { ObjectId = 9999 }],
            [Registration(table.ObjectId, "other", true, 9)],
            [Registration(table.ObjectId, index.Name, false, 9)],
            [valid, Registration(table.ObjectId, "IX_EMAIL", true, 10)],
            [valid with { Definition = new IndexDefinition(index.Name, IndexKind.Hash, true) }],
            [valid with { RootPageId = 0 }],
            [valid with { RootPageId = -1 }],
        ];
        ulong generation = catalog.DdlGeneration;

        foreach (var registrations in invalidSnapshots)
        {
            // Act
            await Should.ThrowAsync<SqlCatalogException>(async () =>
            {
                if (publication == 0)
                {
                    await catalog.CreateIndexAsync(index, registrations, CancellationToken.None);
                }
                else
                {
                    await catalog.PublishTableAsync(table, [index], registrations,
                        replaceExisting: publication == 2, CancellationToken.None);
                }
            });

            // Assert: no description, directory, table alteration or generation was committed.
            catalog.DdlGeneration.ShouldBe(generation);
            catalog.TryGetIndex(table.ObjectId, index.Name, out _).ShouldBeFalse();
            catalog.GetIndexRegistrations().ShouldBe([existing]);
            catalog.Tables.ShouldHaveSingleItem().Columns.Select(column => column.Name).ShouldBe(["id", "email", "age"]);
            var reopened = harness.Reopen();
            reopened.DdlGeneration.ShouldBe(generation);
            reopened.TryGetIndex(table.ObjectId, index.Name, out _).ShouldBeFalse();
            reopened.GetIndexRegistrations().ShouldBe([existing]);
            reopened.Tables.ShouldHaveSingleItem().Columns.Select(column => column.Name).ShouldBe(["id", "email", "age"]);
        }

        // A matching registration uses the catalog's case-insensitive name identity.
        var matching = Registration(table.ObjectId, "IX_EMAIL", true, 9);
        if (publication == 0)
        {
            await catalog.CreateIndexAsync(index, [matching], CancellationToken.None);
        }
        else
        {
            await catalog.PublishTableAsync(table, [index], [matching],
                replaceExisting: publication == 2, CancellationToken.None);
        }
        var published = harness.Reopen();
        published.DdlGeneration.ShouldBe(generation + 1);
        published.TryGetIndex(table.ObjectId, index.Name, out _).ShouldBeTrue();
        published.GetIndexRegistrations().ShouldBe([existing, matching]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Indexes: a stale drop snapshot retains a later unrelated publication")]
    public async Task DropIndex_SnapshotBeforeUnrelatedPublication_ShouldRetainPublishedRegistration()
    {
        // Arrange: DROP captured the directory before the age index published.
        var (catalog, harness, table) = await OpenWithTableAsync();
        await catalog.CreateIndexAsync(new SqlCatalogIndex(table.ObjectId, "ix_email", ["email"], false),
            [Registration(table.ObjectId, "ix_email", false, 7)]);
        var remaining = Array.Empty<BTreeIndexRegistration>();
        var age = Registration(table.ObjectId, "ix_age", false, 9);
        await catalog.CreateIndexAsync(new SqlCatalogIndex(table.ObjectId, "ix_age", ["age"], false), [age]);

        // Act
        await catalog.DropIndexAsync(table.ObjectId, "ix_email", remaining);

        // Assert
        catalog.GetIndexRegistrations().ShouldBe([age]);
        var reopened = harness.Reopen();
        reopened.GetIndexRegistrations().ShouldBe([age]);
        reopened.GetIndexes(table.ObjectId).Single().Name.ShouldBe("ix_age");
    }
}
