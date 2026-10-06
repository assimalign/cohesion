using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
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

        public SqlCatalog Open()
        {
            var storage = SqlStorage.Create(
                new NonClosingStream(_data), new NonClosingStream(_journal), new MemoryStream(), "catalog-test");
            return SqlCatalog.Open(storage);
        }

        public SqlCatalog Reopen() => SqlCatalog.Open(OpenCopy());

        /// <summary>
        /// Reads the raw catalog records the way a catalog's load does and returns
        /// the kinds of the record-space format marker records found (4 or 8).
        /// </summary>
        public List<int> MarkerKinds()
        {
            using var storage = OpenCopy();
            using var iterator = storage.GetUnitIterator();
            var kinds = new List<int>();

            while (iterator.MoveNext())
            {
                var reader = new DatabaseKeyReader(iterator.Current.Data.Span);
                int kind = reader.ReadInt32();

                if (kind is 4 or 8)
                {
                    kinds.Add(kind);
                }
            }

            return kinds;
        }

        private SqlStorage OpenCopy()
        {
            var dataCopy = new MemoryStream();
            dataCopy.Write(_data.ToArray());
            var journalCopy = new MemoryStream();
            journalCopy.Write(_journal.ToArray());

            return SqlStorage.Open(dataCopy, journalCopy, new MemoryStream());
        }
    }

    private static (SqlCatalog Catalog, CatalogHarness Harness) OpenFresh()
    {
        var harness = new CatalogHarness();
        return (harness.Open(), harness);
    }

    private static SqlCatalog Reopen(CatalogHarness harness) => harness.Reopen();

    // NonClosingStream lives in TestObjects/ (shared with the index metadata suite).

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Ownership: metadata survives table alteration and reopen")]
    public async Task SchemaOwnership_AfterColumnChanges_ShouldPersistForTableAndIndex()
    {
        var (catalog, harness) = OpenFresh();
        var table = await catalog.CreateTableAsync("dbo", "customers",
            [Column("id", DatabaseType.Int64), Column("legacy", DatabaseType.String)],
            null, DatabaseObjectOwner.Schema, "AppSchema", default);
        await catalog.CreateIndexAsync(new SqlCatalogIndex(table.ObjectId, "ix_customers_id", ["id"], false,
            DatabaseObjectOwner.Schema, "AppSchema"), []);
        await catalog.AddColumnAsync("dbo", "customers", Column("note", DatabaseType.String));
        await catalog.DropColumnAsync("dbo", "customers", "legacy");
        await catalog.CreateTableAsync("dbo", "scratch", [Column("id", DatabaseType.Int64)]);

        SqlCatalog reopened = Reopen(harness);

        reopened.TryGetTable("dbo", "customers", out var persisted).ShouldBeTrue();
        persisted.Owner.ShouldBe(DatabaseObjectOwner.Schema);
        persisted.OwningSchema.ShouldBe("AppSchema");
        persisted.FindColumn("note").ShouldNotBeNull();
        persisted.FindColumn("legacy").ShouldBeNull();
        reopened.TryGetIndex(table.ObjectId, "ix_customers_id", out var index).ShouldBeTrue();
        index.Owner.ShouldBe(DatabaseObjectOwner.Schema);
        index.OwningSchema.ShouldBe("AppSchema");
        reopened.TryGetTable("dbo", "scratch", out var scratch).ShouldBeTrue();
        scratch.Owner.ShouldBe(DatabaseObjectOwner.Adhoc);
        scratch.OwningSchema.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Ownership: legacy records remain ad-hoc")]
    public void LegacyRecords_WithoutOwnershipSuffix_ShouldRemainAdhoc()
    {
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "legacy-catalog");
        var table = new DatabaseKeyWriter();
        table.AppendInt32(1).AppendInt64(1).AppendString("dbo", Collation.Binary)
            .AppendString("legacy", Collation.Binary).AppendInt32(1)
            .AppendString("id", Collation.Binary).AppendInt8((sbyte)DatabaseType.Int64)
            .AppendInt32(-1).AppendInt32(-1).AppendInt32(-1).AppendBoolean(true)
            .AppendNull().AppendInt32(0);
        var index = new DatabaseKeyWriter();
        index.AppendInt32(5).AppendInt64(1).AppendString("ix_legacy_id", Collation.Binary)
            .AppendBoolean(false).AppendInt32(1).AppendString("id", Collation.Binary);
        using (var transaction = storage.BeginTransaction())
        {
            storage.InsertRow(transaction, table.ToArray());
            storage.InsertRow(transaction, index.ToArray());
            transaction.Commit();
        }

        SqlCatalog catalog = SqlCatalog.Open(storage);

        catalog.TryGetTable("dbo", "legacy", out var loadedTable).ShouldBeTrue();
        loadedTable.Owner.ShouldBe(DatabaseObjectOwner.Adhoc);
        loadedTable.OwningSchema.ShouldBeNull();
        catalog.TryGetIndex(1, "ix_legacy_id", out var loadedIndex).ShouldBeTrue();
        loadedIndex.Owner.ShouldBe(DatabaseObjectOwner.Adhoc);
        loadedIndex.OwningSchema.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Ownership: inconsistent metadata is rejected")]
    [InlineData(DatabaseObjectOwner.Schema, null)]
    [InlineData(DatabaseObjectOwner.Schema, " ")]
    [InlineData(DatabaseObjectOwner.Adhoc, "AppSchema")]
    [InlineData((DatabaseObjectOwner)2, null)]
    public void Ownership_WithInvalidMetadata_ShouldReject(DatabaseObjectOwner owner, string? owningSchema)
    {
        Should.Throw<ArgumentException>(() => new SqlCatalogTable(1, "dbo", "customers",
            [Column("id", DatabaseType.Int64)], owner: owner, owningSchema: owningSchema));
        Should.Throw<ArgumentException>(() => new SqlCatalogIndex(1, "ix_customers_id", ["id"], false,
            owner, owningSchema));
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Schema state: a fresh catalog has no applied schema")]
    public void Open_EmptyCatalog_ShouldHaveNoSchemaState()
    {
        // Arrange / Act
        var (catalog, _) = OpenFresh();

        // Assert
        catalog.SchemaState.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Schema state: large documents and replacements survive reopen")]
    public async Task SaveSchemaState_AcrossReopen_ShouldReplaceCompleteDocument()
    {
        // Arrange
        var (catalog, harness) = OpenFresh();
        string largeDocument = "{\"schema\":\"" + new string('x', 20_000) + "\"}";
        var first = new SqlCatalogSchemaState("sha256:first", largeDocument);

        // Act / Assert: the canonical document spans catalog records and reassembles on open.
        await catalog.SaveSchemaStateAsync(first);
        var firstReopen = Reopen(harness);
        firstReopen.SchemaState.ShouldNotBeNull();
        firstReopen.SchemaState.ContentHash.ShouldBe("sha256:first");
        firstReopen.SchemaState.CanonicalDocument.ShouldBe(largeDocument);

        // Act / Assert: replacement removes every old chunk and persists hash + document together.
        const string replacementDocument = "{\"schema\":\"replacement\"}";
        await catalog.SaveSchemaStateAsync(new SqlCatalogSchemaState("sha256:replacement", replacementDocument));
        var replacementReopen = Reopen(harness);
        replacementReopen.SchemaState.ShouldNotBeNull();
        replacementReopen.SchemaState.ContentHash.ShouldBe("sha256:replacement");
        replacementReopen.SchemaState.CanonicalDocument.ShouldBe(replacementDocument);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Schema state: invalid values are rejected")]
    public void CreateSchemaState_InvalidValues_ShouldThrow()
    {
        // Act / Assert
        Should.Throw<ArgumentException>(() => new SqlCatalogSchemaState("", "{}"));
        Should.Throw<ArgumentException>(() => new SqlCatalogSchemaState("sha256:value", ""));
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

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Format marker: version 4 and later persist under a record kind earlier catalogs refuse (#1099)")]
    public async Task SetRecordSpaceFormatVersion_FromVersionFour_ShouldUseFencedRecordKind()
    {
        // Arrange
        var (catalog, harness) = OpenFresh();
        harness.MarkerKinds().ShouldBeEmpty();

        // Act + Assert: versions 1-3 keep the kind-4 record every catalog reads...
        await catalog.SetRecordSpaceFormatVersionAsync(3);
        harness.MarkerKinds().ShouldBe([4]);
        Reopen(harness).RecordSpaceFormatVersion.ShouldBe(3);

        // ...version 4 rewrites it as a kind-8 record. Catalogs before format 4
        // (through 10.0.0-preview.1) load kinds 1-7 only and refuse any other,
        // so an engine that would write format-3 index keys cannot open it...
        await catalog.SetRecordSpaceFormatVersionAsync(4);
        harness.MarkerKinds().ShouldBe([8]);
        Reopen(harness).RecordSpaceFormatVersion.ShouldBe(4);

        // ...later versions stay behind the same fence...
        await catalog.SetRecordSpaceFormatVersionAsync(5);
        harness.MarkerKinds().ShouldBe([8]);
        Reopen(harness).RecordSpaceFormatVersion.ShouldBe(5);

        // ...and lowering the marker below 4 restores the single kind-4 record.
        await catalog.SetRecordSpaceFormatVersionAsync(3);
        harness.MarkerKinds().ShouldBe([4]);
        Reopen(harness).RecordSpaceFormatVersion.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Record size: a column addition past one catalog record is refused and the table is kept")]
    public async Task AddColumn_DefinitionPastTheRecordSize_ShouldThrowAndKeepTheTable()
    {
        // Arrange: each long-named column adds about a hundred bytes to the table's record,
        // which must fit one slotted-page slot.
        var (catalog, harness) = OpenFresh();
        await catalog.CreateTableAsync("dbo", "wide", [Column("id", DatabaseType.Int64)]);
        string prefix = new('w', 100);
        int added = 0;
        SqlCatalogException? failure = null;

        // Act
        for (int column = 1; column <= 200 && failure is null; column++)
        {
            try
            {
                await catalog.AddColumnAsync("dbo", "wide", Column($"{prefix}{column}", DatabaseType.Int32));
                added++;
            }
            catch (SqlCatalogException exception)
            {
                failure = exception;
            }
        }

        // Assert: a catalog error naming the table, and the definition is the last one
        // that fit, in memory and after a reopen; smaller changes still succeed.
        failure.ShouldNotBeNull();
        failure.Message.ShouldContain("table 'dbo.wide'", Case.Sensitive);
        failure.Message.ShouldContain(SlottedPage.MaxRecordSize.ToString(CultureInfo.InvariantCulture), Case.Sensitive);
        added.ShouldBeGreaterThan(40);
        catalog.TryGetTable("dbo", "wide", out var table).ShouldBeTrue();
        table.Columns.Count.ShouldBe(1 + added);
        Reopen(harness).TryGetTable("dbo", "wide", out var persisted).ShouldBeTrue();
        persisted.Columns.Count.ShouldBe(1 + added);
        await catalog.DropColumnAsync("dbo", "wide", $"{prefix}{added}");
        await catalog.AddColumnAsync("dbo", "wide", Column("small", DatabaseType.Int32));
        Reopen(harness).TryGetTable("dbo", "wide", out var shrunk).ShouldBeTrue();
        shrunk.FindColumn("small").ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Record size: a table definition past one catalog record is refused and nothing is created")]
    public async Task CreateTable_DefinitionPastTheRecordSize_ShouldThrowAndCreateNothing()
    {
        // Arrange
        var (catalog, harness) = OpenFresh();
        var columns = Enumerable.Range(0, 120)
            .Select(column => Column($"{new string('n', 100)}{column}", DatabaseType.String))
            .ToList();

        // Act
        var failure = await Should.ThrowAsync<SqlCatalogException>(async () =>
            await catalog.CreateTableAsync("dbo", "huge", columns));

        // Assert
        failure.Message.ShouldContain("table 'dbo.huge'", Case.Sensitive);
        catalog.TryGetTable("dbo", "huge", out _).ShouldBeFalse();
        Reopen(harness).TryGetTable("dbo", "huge", out _).ShouldBeFalse();
        await catalog.CreateTableAsync("dbo", "huge", columns.Take(10).ToList());
        Reopen(harness).TryGetTable("dbo", "huge", out var created).ShouldBeTrue();
        created.Columns.Count.ShouldBe(10);
    }
}
