using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

public partial class SqlCatalogTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Recovery: loading never deletes sequence metadata")]
    [InlineData(false)][InlineData(true)]
    public async Task Open_ReclamationMetadata_ShouldRemainReadOnly(bool liveCollision)
    {
        // Arrange
        var journal = new MemoryStream();
        using var storage = SqlStorage.Create(new MemoryStream(), journal, new MemoryStream(), "identity-load-review");
        var catalog = SqlCatalog.Open(storage);
        await catalog.CreateSchemaAsync(new SqlCatalogNamespace("dbo"));
        var table = await catalog.CreateTableAsync("dbo", "t", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32),
            identity: new SqlCatalogIdentity(false, 1, 1))]);
        catalog.AllocateIdentity(table).ShouldBe(1);
        if (!liveCollision)
        {
            await catalog.DropTableAsync("dbo", "t");
            var orphan = await catalog.CreateSequenceAsync("dbo", "orphan", new(), ownerTableId: table.ObjectId, ownerColumn: "id");
            catalog.AllocateSequence(orphan).ShouldBe(1);
        }
        else
        {
            using var transaction = storage.BeginTransaction();
            storage.InsertRow(transaction, table.ObjectId, SqlReclamationRecord.Encode(table.ObjectId, "dbo", "t"));
            transaction.Commit();
        }
        var before = ReadRecords(storage);
        long journalBefore = journal.Length;
        // Act
        SqlCatalog? restored = null;
        Exception? failure = null;
        if (liveCollision) { failure = Should.Throw<Exception>(() => SqlCatalog.Open(storage)); }
        else { restored = SqlCatalog.Open(storage); }
        // Assert: compare every kind/location/value, not only the visible catalog.
        ReadRecords(storage).ShouldBe(before);
        journal.Length.ShouldBe(journalBefore);
        if (failure is not null) { failure.ShouldBeOfType<InvalidDataException>(); }
        if (restored is not null)
        {
            restored.Sequences.ShouldBeEmpty();
            await restored.CompleteTableReclamationAsync(table.ObjectId);
            ReadRecords(storage).Length.ShouldBe(before.Length - 3);
        }

        static string[] ReadRecords(SqlStorage source)
        {
            using var iterator = source.GetUnitIterator();
            var records = new System.Collections.Generic.List<string>();
            while (iterator.MoveNext()) { records.Add(Convert.ToHexString(iterator.Current.Data.Span)); }
            return records.Order(StringComparer.Ordinal).ToArray();
        }
    }
}
