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

public partial class SqlCatalogTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Sequence: publication rechecks a name claimed after table reservation")]
    public async Task PublishTableAsync_SequenceWinsNameRace_ShouldRefuseWithoutChangingSequence()
    {
        // Arrange
        var (catalog, harness) = OpenFresh();
        await catalog.CreateSchemaAsync(new SqlCatalogNamespace("dbo"));
        var table = await catalog.ReserveTableAsync("dbo", "shared", [Column("id", DatabaseType.Int64)], [], [], DatabaseObjectOwner.Adhoc, null);
        var sequence = await catalog.CreateSequenceAsync("dbo", "shared", new());
        ulong generation = catalog.DdlGeneration;

        // Act / Assert
        var failure = await Should.ThrowAsync<SqlCatalogException>(() => catalog.PublishTableAsync(table, [], []).AsTask());
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03070");
        catalog.DdlGeneration.ShouldBe(generation);
        catalog.AllocateSequence(sequence).ShouldBe(1);
        var restored = Reopen(harness);
        restored.Tables.ShouldBeEmpty();
        restored.AllocateSequence(restored.FindSequence("dbo", "shared")!).ShouldBe(33);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Sequence: oversized definitions fail before reserving an identity")]
    public async Task CreateSequenceAsync_OversizedRecord_ShouldLeaveCatalogUnchanged()
    {
        // Arrange
        var (catalog, _) = OpenFresh();
        await catalog.CreateSchemaAsync(new SqlCatalogNamespace("dbo"));
        ulong generation = catalog.DdlGeneration;

        // Act / Assert
        var failure = await Should.ThrowAsync<SqlCatalogException>(() => catalog.CreateSequenceAsync("dbo", new string('s', 16 * 1024), new()).AsTask());
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03072");
        catalog.DdlGeneration.ShouldBe(generation);
        catalog.Sequences.ShouldBeEmpty();
        (await catalog.CreateSequenceAsync("dbo", "s", new())).ObjectId.ShouldBe(1UL);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Sequence: corrupt definitions and reservations fail closed")]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)]
    [InlineData(6)][InlineData(7)][InlineData(8)][InlineData(9)][InlineData(10)][InlineData(11)]
    public async Task Open_InvalidSequenceRecord_ShouldRefuse(int scenario)
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "sequence-corrupt");
        var catalog = SqlCatalog.Open(storage);
        await catalog.CreateSchemaAsync(new SqlCatalogNamespace("dbo"));
        var sequence = await catalog.CreateSequenceAsync("dbo", "s", new(minimum: 1, maximum: 100));
        byte[] record;
        if (scenario <= 3)
        {
            record = new DatabaseKeyWriter().AppendInt32(19).AppendInt64(scenario == 0 ? 999 : (long)sequence.ObjectId)
                .AppendInt64(scenario == 1 ? 0 : scenario == 2 ? 101 : 32).AppendBoolean(true).ToArray();
        }
        else
        {
            long id = scenario == 4 ? 0 : scenario == 5 ? (long)sequence.ObjectId : (long)sequence.ObjectId + 1;
            var writer = new DatabaseKeyWriter().AppendInt32(18).AppendInt64(id)
                .AppendString(scenario == 6 ? "absent" : "dbo", Collation.Binary)
                .AppendString(scenario == 7 ? "S" : "other", Collation.Binary)
                .AppendInt32((int)DatabaseType.Int64 + (scenario == 11 ? 256 : 0)).AppendInt64(1).AppendInt64(scenario == 8 ? 0 : 1)
                .AppendInt64(1).AppendInt64(100).AppendInt64(scenario == 9 ? 0 : 1).AppendBoolean(false);
            if (scenario == 10) { writer.AppendInt32(1); }
            record = writer.ToArray();
        }
        using (var transaction = storage.BeginTransaction())
        {
            storage.InsertRow(transaction, record);
            if (scenario == 3) { storage.InsertRow(transaction, record); }
            transaction.Commit();
        }

        // Act / Assert
        Should.Throw<SqlCatalogException>(() => SqlCatalog.Open(storage)).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS06061");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Sequence: canceled mutations leave no publication or reservation")]
    public async Task Catalog_CanceledMutations_ShouldKeepCurrentIncarnation()
    {
        // Arrange
        var (catalog, harness) = OpenFresh();
        await catalog.CreateSchemaAsync(new SqlCatalogNamespace("dbo"));
        var sequence = await catalog.CreateSequenceAsync("dbo", "s", new());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        ulong generation = catalog.DdlGeneration;

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(() => catalog.CreateSequenceAsync("dbo", "other", new(), canceled.Token).AsTask());
        await Should.ThrowAsync<OperationCanceledException>(() => catalog.AlterSequenceAsync(sequence, new(), cancellationToken: canceled.Token).AsTask());
        await Should.ThrowAsync<OperationCanceledException>(() => catalog.DropSequenceAsync(sequence, canceled.Token).AsTask());
        catalog.DdlGeneration.ShouldBe(generation);
        catalog.AllocateSequence(sequence).ShouldBe(1);
        var restored = Reopen(harness);
        restored.Sequences.ShouldHaveSingleItem();
        restored.AllocateSequence(restored.FindSequence("dbo", "s")!).ShouldBe(33);
    }
}
