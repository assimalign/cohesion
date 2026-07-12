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
/// Tests for the relational catalog (#175): table lifecycle, metadata persistence
/// across reopen, object-id stability, constraint validation, and index
/// registration round-trips.
/// </summary>
public class SqlCatalogTests
{
    private static SqlCatalogColumn Column(string name, DatabaseType type, bool nullable = true, string? defaultLiteral = null)
        => new(name, new DatabaseTypeInfo(type), nullable, defaultLiteral);

    /// <summary>
    /// Keeps the catalog's data and journal streams reachable so tests can reopen
    /// from them mid-life: commits are no-force, so the journal is a required part
    /// of the persisted state — recovery replays it on reopen.
    /// </summary>
    private sealed class CatalogHarness
    {
        private readonly MemoryStream _data = new();
        private readonly MemoryStream _journal = new();

        public ISqlCatalog Open()
        {
            var storage = SqlStorage.Create(
                new NonClosingStream(_data), new NonClosingStream(_journal), new MemoryStream(), "catalog-test");
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

    private static (ISqlCatalog Catalog, CatalogHarness Harness) OpenFresh()
    {
        var harness = new CatalogHarness();
        return (harness.Open(), harness);
    }

    private static ISqlCatalog Reopen(CatalogHarness harness) => harness.Reopen();

    /// <summary>
    /// Lets tests capture bytes after the storage disposes its streams.
    /// </summary>
    private sealed class NonClosingStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonClosingStream(MemoryStream inner) => _inner = inner;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
            // deliberately keep the inner stream alive
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - CreateTable: assigns object ids and exposes the table")]
    public async Task CreateTable_NewTable_ShouldAssignIdentityAndExpose()
    {
        // Arrange
        var (catalog, _) = OpenFresh();

        // Act
        var users = await catalog.CreateTableAsync("dbo", "users", new[]
        {
            Column("id", DatabaseType.Int64, nullable: false),
            Column("name", DatabaseType.String),
        }, primaryKeyColumns: new[] { "id" });

        var orders = await catalog.CreateTableAsync("dbo", "orders", new[] { Column("id", DatabaseType.Int64) });

        // Assert
        users.ObjectId.ShouldBe(1UL);
        orders.ObjectId.ShouldBe(2UL);
        catalog.Tables.Count.ShouldBe(2);
        catalog.TryGetTable("DBO", "USERS", out var found).ShouldBeTrue(); // case-insensitive
        found.PrimaryKeyColumns.ShouldBe(new[] { "id" });
        found.FindColumn("NAME").ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Validation: duplicates and bad definitions are rejected")]
    public async Task CreateTable_InvalidDefinitions_ShouldThrow()
    {
        // Arrange
        var (catalog, _) = OpenFresh();
        await catalog.CreateTableAsync("dbo", "t", new[] { Column("id", DatabaseType.Int32) });

        // Act / Assert
        await Should.ThrowAsync<SqlCatalogException>(async () =>
            await catalog.CreateTableAsync("dbo", "T", new[] { Column("id", DatabaseType.Int32) })); // duplicate (case-insensitive)

        await Should.ThrowAsync<SqlCatalogException>(async () =>
            await catalog.CreateTableAsync("dbo", "bad", new[] { Column("a", DatabaseType.Int32), Column("A", DatabaseType.Int32) })); // duplicate column

        await Should.ThrowAsync<SqlCatalogException>(async () =>
            await catalog.CreateTableAsync("dbo", "bad2", new[] { Column("a", DatabaseType.Int32) }, new[] { "missing" })); // pk not a column
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Persistence: tables, columns, and ids survive reopen")]
    public async Task Reopen_PersistedCatalog_ShouldReloadEverything()
    {
        // Arrange
        var (catalog, harness) = OpenFresh();
        await catalog.CreateTableAsync("dbo", "users", new[]
        {
            Column("id", DatabaseType.Int64, nullable: false),
            Column("name", DatabaseType.String, defaultLiteral: "anonymous"),
            new SqlCatalogColumn("balance", new DatabaseTypeInfo(DatabaseType.Decimal, precision: 18, scale: 4), isNullable: false),
        }, new[] { "id" });
        await catalog.DropTableAsync("dbo", "users") ; // exercise delete persistence
        var final = await catalog.CreateTableAsync("dbo", "accounts", new[]
        {
            Column("id", DatabaseType.Int64, nullable: false),
            new SqlCatalogColumn("email", new DatabaseTypeInfo(DatabaseType.String, maxLength: 255)),
        }, new[] { "id" });

        // Act
        var reopened = Reopen(harness);

        // Assert: only the live table, with every column attribute intact, and the
        // object-id counter past everything ever assigned.
        reopened.Tables.Count.ShouldBe(1);
        reopened.TryGetTable("dbo", "accounts", out var accounts).ShouldBeTrue();
        accounts.ObjectId.ShouldBe(final.ObjectId);
        accounts.Columns.Count.ShouldBe(2);
        accounts.Columns[1].Type.MaxLength.ShouldBe(255);
        accounts.PrimaryKeyColumns.ShouldBe(new[] { "id" });

        var next = await reopened.CreateTableAsync("dbo", "next", new[] { Column("x", DatabaseType.Int32) });
        next.ObjectId.ShouldBeGreaterThan(final.ObjectId);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Alter: add and drop columns persist across reopen")]
    public async Task AlterTable_AddAndDropColumns_ShouldPersist()
    {
        // Arrange
        var (catalog, harness) = OpenFresh();
        await catalog.CreateTableAsync("dbo", "t", new[]
        {
            Column("id", DatabaseType.Int64, nullable: false),
            Column("legacy", DatabaseType.String),
        }, new[] { "id" });

        // Act
        await catalog.AddColumnAsync("dbo", "t", Column("email", DatabaseType.String));
        await catalog.DropColumnAsync("dbo", "t", "legacy");

        // Assert (live)
        catalog.TryGetTable("dbo", "t", out var live).ShouldBeTrue();
        live.Columns.Select(c => c.Name).ShouldBe(new[] { "id", "email" });

        // Guards
        await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.AddColumnAsync("dbo", "t", Column("email", DatabaseType.String)));
        await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.DropColumnAsync("dbo", "t", "id")); // primary key
        await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.DropColumnAsync("dbo", "missing", "x"));

        // Assert (reopened)
        var reopened = Reopen(harness);
        reopened.TryGetTable("dbo", "t", out var persisted).ShouldBeTrue();
        persisted.Columns.Select(c => c.Name).ShouldBe(new[] { "id", "email" });
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Indexes: registrations round-trip across reopen")]
    public async Task SaveIndexRegistrations_ShouldRoundTripAcrossReopen()
    {
        // Arrange
        var (catalog, harness) = OpenFresh();
        var registrations = new List<BTreeIndexRegistration>
        {
            new(1, new IndexDefinition("pk_users", IndexKind.BTree, IsUnique: true), 7),
            new(2, new IndexDefinition("ix_orders_date"), 12),
        };

        // Act
        await catalog.SaveIndexRegistrationsAsync(registrations);
        await catalog.SaveIndexRegistrationsAsync(registrations); // idempotent rewrite path

        var reopened = Reopen(harness);

        // Assert
        var loaded = reopened.GetIndexRegistrations();
        loaded.Count.ShouldBe(2);
        loaded[0].Definition.Name.ShouldBe("pk_users");
        loaded[0].Definition.IsUnique.ShouldBeTrue();
        loaded[0].RootPageId.ShouldBe(7);
        loaded[1].ObjectId.ShouldBe(2UL);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Growth: many tables relocate records and reload cleanly")]
    public async Task CreateTable_ManyTables_ShouldPersistAtVolume()
    {
        // Arrange: enough tables (with fat column lists) to spill multiple pages.
        var (catalog, harness) = OpenFresh();

        for (int i = 0; i < 50; i++)
        {
            var columns = Enumerable.Range(0, 20)
                .Select(c => Column($"column_{c}_with_a_reasonably_long_name", DatabaseType.String))
                .ToList();
            await catalog.CreateTableAsync("dbo", $"table_{i}", columns);
        }

        // Act
        var reopened = Reopen(harness);

        // Assert
        reopened.Tables.Count.ShouldBe(50);
        reopened.TryGetTable("dbo", "table_49", out var last).ShouldBeTrue();
        last.Columns.Count.ShouldBe(20);
    }
}
