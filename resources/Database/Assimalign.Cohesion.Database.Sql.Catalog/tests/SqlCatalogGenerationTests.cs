using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

public sealed class SqlCatalogGenerationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - DDL generation: every mutation advances durably")]
    public async Task DdlGeneration_AllCatalogMutations_ShouldAdvanceWithEachCommit()
    {
        // Arrange
        using var data = new MemoryStream();
        using var journal = new MemoryStream();
        using var storage = SqlStorage.Create(new NonClosingStream(data), new NonClosingStream(journal),
            new MemoryStream(), "generation");
        var catalog = SqlCatalog.Open(storage);
        catalog.DdlGeneration.ShouldBe(0UL);

        // Act / Assert: each publication, including reservations and ad-hoc DDL, changes the counter.
        await catalog.SetRecordSpaceFormatVersionAsync(8, CancellationToken.None);
        catalog.DdlGeneration.ShouldBe(1UL);
        var table = await catalog.ReserveTableAsync("dbo", "owned", [Column("id")], ["id"], [],
            DatabaseObjectOwner.Schema, "App", CancellationToken.None);
        catalog.DdlGeneration.ShouldBe(2UL);
        catalog.Tables.ShouldBeEmpty();
        await catalog.PublishTableAsync(table, [], [], cancellationToken: CancellationToken.None);
        catalog.DdlGeneration.ShouldBe(3UL);
        await catalog.AddColumnAsync("dbo", "owned", Column("note"), CancellationToken.None);
        catalog.DdlGeneration.ShouldBe(4UL);
        await catalog.DropColumnAsync("dbo", "owned", "note", CancellationToken.None);
        catalog.DdlGeneration.ShouldBe(5UL);
        await catalog.AddConstraintAsync("dbo", "owned",
            new SqlCatalogConstraint("ck_id", SqlCatalogConstraintKind.Check, ["id"], checkExpression: "id > 0"),
            CancellationToken.None);
        catalog.DdlGeneration.ShouldBe(6UL);
        await catalog.DropConstraintAsync("dbo", "owned", "ck_id", CancellationToken.None);
        catalog.DdlGeneration.ShouldBe(7UL);
        var registration = new BTreeIndexRegistration(table.ObjectId, new IndexDefinition("ix_id"), 42);
        await catalog.CreateIndexAsync(new SqlCatalogIndex(table.ObjectId, "ix_id", ["id"], false,
            DatabaseObjectOwner.Schema, "App"), [registration], CancellationToken.None);
        catalog.DdlGeneration.ShouldBe(8UL);
        await catalog.DropIndexAsync(table.ObjectId, "ix_id", [], CancellationToken.None);
        catalog.DdlGeneration.ShouldBe(9UL);
        await catalog.CreateTableAsync("dbo", "adhoc", [Column("id")], cancellationToken: CancellationToken.None);
        catalog.DdlGeneration.ShouldBe(10UL);
        await catalog.DropTableAsync("dbo", "adhoc", CancellationToken.None);
        catalog.DdlGeneration.ShouldBe(11UL);
        await catalog.SaveIndexRegistrationsAsync([registration], CancellationToken.None);
        catalog.DdlGeneration.ShouldBe(12UL);
        await catalog.SaveIndexRegistrationsAsync([], CancellationToken.None);
        catalog.DdlGeneration.ShouldBe(13UL);
        await catalog.DropTableAsync("dbo", "owned", CancellationToken.None);
        catalog.DdlGeneration.ShouldBe(14UL);

        using var reopenedStorage = Reopen(data, journal);
        var reopened = SqlCatalog.Open(reopenedStorage);
        reopened.DdlGeneration.ShouldBe(14UL);
        reopened.Tables.ShouldBeEmpty();
        reopened.GetIndexRegistrations().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Schema state: hash, document and generation survive recovery")]
    public async Task SaveSchemaState_AfterDdl_ShouldRetainGenerationAndRejectStaleRecording()
    {
        // Arrange
        using var data = new MemoryStream();
        using var journal = new MemoryStream();
        using var storage = SqlStorage.Create(new NonClosingStream(data), new NonClosingStream(journal),
            new MemoryStream(), "schema-state-generation");
        var catalog = SqlCatalog.Open(storage);
        await catalog.CreateTableAsync("dbo", "owned", [Column("id")], cancellationToken: CancellationToken.None);
        var state = new SqlCatalogSchemaState("sha256:applied", new string('x', 20_000), catalog.DdlGeneration);

        // Act
        await catalog.SaveSchemaStateAsync(state, CancellationToken.None);
        using var initialStorage = Reopen(data, journal);
        var initial = SqlCatalog.Open(initialStorage);
        await catalog.CreateTableAsync("dbo", "adhoc", [Column("id")], cancellationToken: CancellationToken.None);
        await Should.ThrowAsync<SqlCatalogException>(async () =>
            await catalog.SaveSchemaStateAsync(state, CancellationToken.None));
        using var reopenedStorage = Reopen(data, journal);
        var reopened = SqlCatalog.Open(reopenedStorage);

        // Assert
        initial.DdlGeneration.ShouldBe(1UL);
        initial.SchemaState.ShouldNotBeNull();
        initial.SchemaState.Generation.ShouldBe(initial.DdlGeneration);
        initial.SchemaState.ContentHash.ShouldBe(state.ContentHash);
        initial.SchemaState.CanonicalDocument.ShouldBe(state.CanonicalDocument);
        reopened.DdlGeneration.ShouldBe(2UL);
        reopened.SchemaState.ShouldNotBeNull();
        reopened.SchemaState.Generation.ShouldBe(1UL);
        reopened.SchemaState.CanonicalDocument.ShouldBe(state.CanonicalDocument);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - DDL generation: failed first commit rolls back counter and table")]
    public async Task CreateTable_CommitRefused_ShouldKeepGenerationAndPermitRetry()
    {
        // Arrange
        using var data = new MemoryStream();
        using var journal = new MemoryStream();
        var journalHandle = new CatalogDurabilityHandle(journal);
        using var storage = SqlStorage.Create(new StorageStream(new CatalogDurabilityHandle(data)),
            new StorageStream(journalHandle), new StorageStream(new CatalogDurabilityHandle(new MemoryStream())), "generation-rollback");
        var catalog = SqlCatalog.Open(storage);
        journalHandle.SupportsDurableFlush = false;

        // Act: the journal handle has lost its durability capability. The real
        // commit refuses after the catalog has staged the table and counter in its transaction.
        await Should.ThrowAsync<NotSupportedException>(async () =>
            await catalog.CreateTableAsync("dbo", "refused", [Column("id")], cancellationToken: CancellationToken.None));

        // Assert
        catalog.DdlGeneration.ShouldBe(0UL);
        catalog.Tables.ShouldBeEmpty();
        using (var rolledBackStorage = Reopen(data, journal))
        {
            var rolledBack = SqlCatalog.Open(rolledBackStorage);
            rolledBack.DdlGeneration.ShouldBe(0UL);
            rolledBack.Tables.ShouldBeEmpty();
        }

        // Act / Assert: failed publication left no counter location or identity to poison a retry.
        journalHandle.SupportsDurableFlush = true;
        var table = await catalog.CreateTableAsync("dbo", "accepted", [Column("id")], cancellationToken: CancellationToken.None);
        table.ObjectId.ShouldBe(1UL);
        catalog.DdlGeneration.ShouldBe(1UL);
        using var reopenedStorage = Reopen(data, journal);
        var reopened = SqlCatalog.Open(reopenedStorage);
        reopened.DdlGeneration.ShouldBe(1UL);
        reopened.TryGetTable("dbo", "accepted", out _).ShouldBeTrue();
        reopened.TryGetTable("dbo", "refused", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Schema state: failed replacement keeps the prior complete document")]
    public async Task SaveSchemaState_CommitRefused_ShouldKeepPriorDocumentAndGeneration()
    {
        // Arrange
        using var data = new MemoryStream();
        using var journal = new MemoryStream();
        var journalHandle = new CatalogDurabilityHandle(journal);
        using var storage = SqlStorage.Create(new StorageStream(new CatalogDurabilityHandle(data)),
            new StorageStream(journalHandle), new StorageStream(new CatalogDurabilityHandle(new MemoryStream())), "state-rollback");
        var catalog = SqlCatalog.Open(storage);
        await catalog.CreateTableAsync("dbo", "first", [Column("id")], cancellationToken: CancellationToken.None);
        var prior = new SqlCatalogSchemaState("prior", new string('x', 20_000), catalog.DdlGeneration);
        await catalog.SaveSchemaStateAsync(prior, CancellationToken.None);
        await catalog.CreateTableAsync("dbo", "second", [Column("id")], cancellationToken: CancellationToken.None);
        var target = new SqlCatalogSchemaState("target", new string('y', 25_000), catalog.DdlGeneration);
        journalHandle.SupportsDurableFlush = false;

        // Act
        await Should.ThrowAsync<NotSupportedException>(async () =>
            await catalog.SaveSchemaStateAsync(target, CancellationToken.None));
        journalHandle.SupportsDurableFlush = true;
        using var reopenedStorage = Reopen(data, journal);
        var reopened = SqlCatalog.Open(reopenedStorage);

        // Assert
        catalog.SchemaState.ShouldBeSameAs(prior);
        catalog.DdlGeneration.ShouldBe(2UL);
        reopened.DdlGeneration.ShouldBe(2UL);
        reopened.SchemaState.ShouldNotBeNull();
        reopened.SchemaState.ContentHash.ShouldBe(prior.ContentHash);
        reopened.SchemaState.CanonicalDocument.ShouldBe(prior.CanonicalDocument);
        reopened.SchemaState.Generation.ShouldBe(1UL);

        // Act / Assert: replacement can retry using the still-valid previous chunk locations.
        await catalog.SaveSchemaStateAsync(target, CancellationToken.None);
        using var retriedStorage = Reopen(data, journal);
        var retried = SqlCatalog.Open(retriedStorage);
        retried.SchemaState.ShouldNotBeNull();
        retried.SchemaState.CanonicalDocument.ShouldBe(target.CanonicalDocument);
        retried.SchemaState.Generation.ShouldBe(retried.DdlGeneration);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - DDL generation: failed replacement preserves the committed generation")]
    public async Task AddColumn_CommitRefused_ShouldKeepPriorDefinitionAndGeneration()
    {
        // Arrange
        using var data = new MemoryStream();
        using var journal = new MemoryStream();
        var journalHandle = new CatalogDurabilityHandle(journal);
        using var storage = SqlStorage.Create(new StorageStream(new CatalogDurabilityHandle(data)),
            new StorageStream(journalHandle), new StorageStream(new CatalogDurabilityHandle(new MemoryStream())), "generation-replacement-rollback");
        var catalog = SqlCatalog.Open(storage);
        await catalog.CreateTableAsync("dbo", "t", [Column("id")], cancellationToken: CancellationToken.None);
        journalHandle.SupportsDurableFlush = false;

        // Act
        await Should.ThrowAsync<NotSupportedException>(async () =>
            await catalog.AddColumnAsync("dbo", "t", Column("refused"), CancellationToken.None));
        journalHandle.SupportsDurableFlush = true;
        using var reopenedStorage = Reopen(data, journal);
        var reopened = SqlCatalog.Open(reopenedStorage);

        // Assert
        catalog.DdlGeneration.ShouldBe(1UL);
        catalog.TryGetTable("dbo", "t", out var live).ShouldBeTrue();
        live.Columns.Count.ShouldBe(1);
        reopened.DdlGeneration.ShouldBe(1UL);
        reopened.TryGetTable("dbo", "t", out var persisted).ShouldBeTrue();
        persisted.Columns.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - DDL generation: unchanged maintenance and cancellation preserve the counter")]
    public async Task DdlGeneration_UnchangedMaintenanceAndCancellation_ShouldRemainStable()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "generation-noop");
        var catalog = SqlCatalog.Open(storage);
        await catalog.SetRecordSpaceFormatVersionAsync(8, CancellationToken.None);
        var registration = new BTreeIndexRegistration(1, new IndexDefinition("ix"), 42);
        await catalog.SaveIndexRegistrationsAsync([registration], CancellationToken.None);
        ulong generation = catalog.DdlGeneration;

        // Act
        await catalog.SetRecordSpaceFormatVersionAsync(8, CancellationToken.None);
        await catalog.SaveIndexRegistrationsAsync([new BTreeIndexRegistration(1, new IndexDefinition("ix"), 42)], CancellationToken.None);
        await catalog.SaveSchemaStateAsync(new SqlCatalogSchemaState("hash", "{}", generation), CancellationToken.None);
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await catalog.CreateTableAsync("dbo", "cancelled", [Column("id")], cancellationToken: new CancellationToken(true)));

        // Assert
        catalog.DdlGeneration.ShouldBe(generation);
        catalog.SchemaState.ShouldNotBeNull();
        catalog.SchemaState.Generation.ShouldBe(generation);
        catalog.Tables.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - DDL generation: database collation initialization is a committed change")]
    public void Open_NonDefaultCollation_ShouldAdvanceGenerationOnce()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "generation-collation");

        // Act
        var catalog = SqlCatalog.Open(storage, Collation.CaseInsensitive);
        var reopened = SqlCatalog.Open(storage, Collation.CaseInsensitive);

        // Assert
        catalog.DdlGeneration.ShouldBe(1UL);
        reopened.DdlGeneration.ShouldBe(1UL);
        reopened.DefaultCollation.ShouldBe(Collation.CaseInsensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Counter format: a counter without generation is refused")]
    public void Open_PreGenerationCounter_ShouldRefuseUnsupportedFormat()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "old-counter");
        var record = new DatabaseKeyWriter();
        record.AppendInt32(2).AppendInt64(1);
        using (var transaction = storage.BeginTransaction())
        {
            storage.InsertRow(transaction, record.ToArray());
            transaction.Commit();
        }

        // Act / Assert
        Should.Throw<SqlCatalogException>(() => SqlCatalog.Open(storage)).Message.ShouldContain("record-space format 8");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Schema state: conflicting chunk generations are refused")]
    public void Open_SchemaChunksWithDifferentGenerations_ShouldRefuseMixedState()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "mixed-state");
        using (var transaction = storage.BeginTransaction())
        {
            for (int index = 0; index < 2; index++)
            {
                var record = new DatabaseKeyWriter();
                record.AppendInt32(6).AppendString("hash", Collation.Binary)
                    .AppendInt32(index).AppendInt32(2).AppendBinary([123, 125]).AppendInt64(index);
                storage.InsertRow(transaction, record.ToArray());
            }
            transaction.Commit();
        }

        // Act / Assert
        Should.Throw<SqlCatalogException>(() => SqlCatalog.Open(storage)).Message.ShouldContain("one complete document");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - DDL generation: exhaustion refuses writes instead of wrapping")]
    public async Task CreateTable_ExhaustedGeneration_ShouldRefuseWithoutPublishing()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "exhausted-generation");
        var record = new DatabaseKeyWriter();
        record.AppendInt32(2).AppendInt64(1).AppendInt64(-1);
        using (var transaction = storage.BeginTransaction())
        {
            storage.InsertRow(transaction, record.ToArray());
            transaction.Commit();
        }
        var catalog = SqlCatalog.Open(storage);

        // Act
        await Should.ThrowAsync<SqlCatalogException>(async () =>
            await catalog.CreateTableAsync("dbo", "refused", [Column("id")], cancellationToken: CancellationToken.None));

        // Assert
        catalog.DdlGeneration.ShouldBe(ulong.MaxValue);
        catalog.Tables.ShouldBeEmpty();
        SqlCatalog.Open(storage).DdlGeneration.ShouldBe(ulong.MaxValue);
        SqlCatalog.Open(storage).Tables.ShouldBeEmpty();
    }

    private static SqlCatalogColumn Column(string name)
        => new(name, new DatabaseTypeInfo(DatabaseType.Int64), isNullable: false);

    private static SqlStorage Reopen(MemoryStream data, MemoryStream journal)
        => SqlStorage.Open(new MemoryStream(data.ToArray()), new MemoryStream(journal.ToArray()), new MemoryStream());
}
