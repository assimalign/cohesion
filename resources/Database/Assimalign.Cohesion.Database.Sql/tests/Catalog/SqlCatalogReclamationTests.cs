using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

public sealed class SqlCatalogReclamationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - DROP durably publishes a private reclamation record with its unpublication")]
    public async Task DropTable_CommittedUnpublication_ShouldPersistRetiredIdentity()
    {
        // Arrange
        using var data = new MemoryStream();
        using var journal = new MemoryStream();
        using var storage = SqlStorage.Create(data, journal, new MemoryStream(), "reclamation");
        var catalog = SqlCatalog.Open(storage);
        var table = await catalog.CreateTableAsync("dbo", "t", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32))]);

        // Act
        await catalog.DropTableAsync("dbo", "t");
        catalog.GetPendingTableReclamations().ShouldBe([table.ObjectId]);
        using var recoveredStorage = SqlStorage.Open(Copy(data), Copy(journal), new MemoryStream());
        var recovered = SqlCatalog.Open(recoveredStorage);

        // Assert: the intent has its own owner page; the name may immediately acquire a new identity.
        recovered.TryGetTable("dbo", "t", out _).ShouldBeFalse();
        recovered.GetPendingTableReclamations().ShouldBe([table.ObjectId]);
        using var iterator = recoveredStorage.GetUnitIterator(table.ObjectId);
        iterator.MoveNext().ShouldBeTrue();
        var reader = new DatabaseKeyReader(iterator.Current.Data.Span);
        reader.ReadInt32().ShouldBe(SqlReclamationRecord.Kind);
        SqlReclamationRecord.Decode(ref reader).ShouldBe((table.ObjectId, "dbo", "t"));
        iterator.MoveNext().ShouldBeFalse();
        var replacement = await recovered.CreateTableAsync("dbo", "t", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32))]);
        replacement.ObjectId.ShouldBeGreaterThan(table.ObjectId);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Pending reclamation snapshots survive cancellation and retire independently of replacement names")]
    public async Task CompleteTableReclamation_CanceledThenRepeated_ShouldPreserveCommittedState()
    {
        // Arrange
        using var data = new MemoryStream();
        using var journal = new MemoryStream();
        using var storage = SqlStorage.Create(data, journal, new MemoryStream(), "retirement");
        var catalog = SqlCatalog.Open(storage);
        var table = await catalog.CreateTableAsync("dbo", "t", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32))]);
        await catalog.DropTableAsync("dbo", "t", CancellationToken.None);
        var snapshot = catalog.GetPendingTableReclamations();
        var replacement = await catalog.CreateTableAsync("dbo", "t", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32))]);
        ulong generation = catalog.DdlGeneration;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act / Assert: canceled retirement changes neither cached nor persisted state.
        await Should.ThrowAsync<OperationCanceledException>(async () => await catalog.CompleteTableReclamationAsync(table.ObjectId, canceled.Token));
        catalog.GetPendingTableReclamations().ShouldBe([table.ObjectId]);
        using (var canceledStorage = SqlStorage.Open(Copy(data), Copy(journal), new MemoryStream()))
        {
            SqlCatalog.Open(canceledStorage).GetPendingTableReclamations().ShouldBe([table.ObjectId]);
        }
        await catalog.CompleteTableReclamationAsync(table.ObjectId, CancellationToken.None);
        await catalog.CompleteTableReclamationAsync(table.ObjectId, CancellationToken.None);
        await catalog.CompleteTableReclamationAsync(replacement.ObjectId, CancellationToken.None);

        // Assert: snapshots are stable, retirement is idempotent bookkeeping, and the live id is untouched.
        snapshot.ShouldBe([table.ObjectId]);
        catalog.GetPendingTableReclamations().ShouldBeEmpty();
        catalog.DdlGeneration.ShouldBe(generation);
        using var recoveredStorage = SqlStorage.Open(Copy(data), Copy(journal), new MemoryStream());
        var recovered = SqlCatalog.Open(recoveredStorage);
        recovered.GetPendingTableReclamations().ShouldBeEmpty();
        recovered.TryGetTable("dbo", "t", out var live).ShouldBeTrue();
        live.ObjectId.ShouldBe(replacement.ObjectId);
        recovered.DdlGeneration.ShouldBe(generation);
        recoveredStorage.GetOwnerPages(table.ObjectId).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - A live table cannot also be a pending reclamation identity")]
    public async Task Open_LiveAndRetiredIdentity_ShouldRefuseCorruptCatalog()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "corrupt");
        var catalog = SqlCatalog.Open(storage);
        var table = await catalog.CreateTableAsync("dbo", "t", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32))]);
        using (var transaction = storage.BeginTransaction())
        {
            storage.InsertRow(transaction, table.ObjectId, SqlReclamationRecord.Encode(table.ObjectId, "dbo", "t"));
            transaction.Commit();
        }

        // Act / Assert
        Should.Throw<InvalidDataException>(() => SqlCatalog.Open(storage));
    }

    private static MemoryStream Copy(MemoryStream original)
    {
        var copy = new MemoryStream();
        copy.Write(original.ToArray());
        copy.Position = 0;
        return copy;
    }
}
