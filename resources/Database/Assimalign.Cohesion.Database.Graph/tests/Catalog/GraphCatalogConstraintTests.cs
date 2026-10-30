using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Catalog.Tests;

public sealed class GraphCatalogConstraintTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Catalog] - Codec: v2 is symmetric for all record kinds and refuses v1")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Codec_VersionTwo_ShouldRoundTripAndRefuseEarlierCatalogs(int kind)
    {
        // Arrange
        Guid id = Guid.NewGuid();
        CatalogRecord record = kind switch
        {
            0 => new(label: new(id, "N")),
            1 => new(relationshipType: new(id, "T", DatabaseObjectOwner.Schema, "schema")),
            2 => new(property: new(id, "p", DatabaseType.Int64, true)),
            3 => new(index: new(id, "index", "p")),
            4 => new(constraint: new(id, "typed", "p", DatabaseType.Int64)),
            _ => new(constraint: new(id, "exists", "p")),
        };

        // Act / Assert
        byte[] bytes = GraphCatalogCodec.Encode(record, new TransactionSequence(7));
        bytes[17].ShouldBe((byte)2);
        GraphCatalogCodec.Decode(bytes).ShouldBe(record);
        bytes[17] = 1;
        var failure = Should.Throw<GraphCatalogException>(() => GraphCatalogCodec.Decode(bytes));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG06019");
        failure.Message.ShouldContain("Recreate the database", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Catalog] - Codec: malformed constraint payloads refuse with catalog corruption")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Decode_MalformedConstraint_ShouldRefuseTruncationEnumsAndTrailingBytes(int damage)
    {
        // Arrange
        byte[] bytes = GraphCatalogCodec.Encode(new CatalogRecord(constraint:
            new GraphConstraintMetadata(Guid.NewGuid(), "typed", "p", DatabaseType.Int64)), new TransactionSequence(7));
        if (damage == 0) { bytes = bytes[..^1]; }
        else if (damage == 1) { bytes[^1] = 254; }
        else { bytes = [.. bytes, 0]; }

        // Act / Assert
        var failure = Should.Throw<GraphCatalogException>(() => GraphCatalogCodec.Decode(bytes));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG06006");
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Catalog] - Recovery: named create/drop crash images preserve committed rules and scrub abandoned rules")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Recover_ConstraintAndIndexCrashImages_ShouldRetainOnlyCommittedStateAsync(bool drop, bool commit)
    {
        // Arrange
        using var data = new MemoryStream();
        using var journal = new MemoryStream();
        using var storage = GraphStorage.Create(data, journal, new MemoryStream(), "crash");
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        var catalog = GraphCatalog.Open(storage, coordinator);
        var store = GraphStore.Open(storage, coordinator);
        Guid id = Guid.NewGuid();
        var setup = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await catalog.SaveLabelAsync(new(id, "N"), setup);
        await catalog.CreateConstraintAsync(new(id, "exists", "p"), setup);
        if (drop) { await Create(setup); }
        await coordinator.CommitAsync(setup);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        if (drop)
        {
            await catalog.DeleteConstraintAsync("typed", writer);
            await catalog.DeleteIndexAsync(id, "index", writer);
            await store.DropIndexAsync("N", "p", writer);
        }
        else { await Create(writer); }
        if (commit) { await coordinator.CommitAsync(writer); }
        storage.FlushPendingCommits();
        storage.WriteBackDirtyPages(int.MaxValue);

        // Act: reopen a byte-for-byte crash image, then reopen its recovery checkpoint again.
        using var recoveredData = Copy(data);
        using var recoveredJournal = Copy(journal);
        for (int reopen = 0; reopen < 2; reopen++)
        {
            using var imageData = Copy(recoveredData);
            using var imageJournal = Copy(recoveredJournal);
            using var recoveredStorage = GraphStorage.Open(imageData, imageJournal, new MemoryStream(), checkpointOnOpen: false);
            GraphCatalog.EnsureFormat(recoveredStorage);
            await using var recovered = new TransactionCoordinator(recoveredStorage, recoveredStorage.WriteAheadJournal, recoveredStorage.Records);
            var recovery = recovered.AnalyzeAndScrub();
            var recoveredCatalog = GraphCatalog.Open(recoveredStorage, recovered);
            var recoveredStore = GraphStore.Open(recoveredStorage, recovered);
            await recoveredStore.RecoverIndexesAsync(recovery.Aborted);
            recovered.CompleteRecovery();
            var reader = await recovered.BeginAsync(IsolationLevel.Snapshot);

            // Assert
            bool present = drop != commit;
            (recoveredCatalog.FindConstraint("typed", reader.Snapshot) is not null).ShouldBe(present);
            recoveredCatalog.FindConstraint("exists", reader.Snapshot).ShouldNotBeNull();
            (recoveredCatalog.FindIndex("index", reader.Snapshot) is not null).ShouldBe(present);
            recoveredStore.HasIndex("N", "p", reader.Snapshot).ShouldBe(present);
            recoveredCatalog.GetPropertyKeys(id, reader.Snapshot).ShouldHaveSingleItem().Required.ShouldBeTrue();
            await recovered.RollbackAsync(reader);
            recoveredStorage.FlushPendingCommits();
            recoveredStorage.WriteBackDirtyPages(int.MaxValue);
            recoveredData.SetLength(0);
            recoveredData.Write(imageData.ToArray());
            recoveredJournal.SetLength(0);
            recoveredJournal.Write(imageJournal.ToArray());
        }
        if (!commit) { await coordinator.RollbackAsync(writer); }

        async ValueTask Create(TransactionContext context)
        {
            await catalog.CreateConstraintAsync(new(id, "typed", "p", DatabaseType.Int64), context);
            await catalog.SaveIndexAsync(new(id, "index", "p"), context);
            await store.CreateIndexAsync("N", "p", context);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Catalog] - Constraints: definition drops hide constraints, rollback restores them, and old snapshots retain them")]
    public async Task DeleteDefinitionAsync_ConstraintChildren_ShouldFollowSnapshotAndRollbackAsync()
    {
        // Arrange
        using var storage = GraphStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "children");
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        var catalog = GraphCatalog.Open(storage, coordinator);
        Guid id = Guid.NewGuid();
        var create = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await catalog.SaveLabelAsync(new(id, "N"), create);
        await catalog.CreateConstraintAsync(new(id, "exists", "p"), create);
        await coordinator.CommitAsync(create);
        var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);

        // Act / Assert
        await catalog.DeleteLabelAsync(id, writer);
        catalog.GetConstraints(writer.Snapshot).ShouldBeEmpty();
        catalog.FindConstraint("exists", reader.Snapshot).ShouldNotBeNull();
        await coordinator.RollbackAsync(writer);
        var retry = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        catalog.GetConstraints(retry.Snapshot).ShouldHaveSingleItem().Name.ShouldBe("exists");
        await catalog.DeleteConstraintAsync("exists", retry);
        await coordinator.CommitAsync(retry);
        catalog.FindConstraint("exists", reader.Snapshot).ShouldNotBeNull();
        await coordinator.RollbackAsync(reader);
        var fresh = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await catalog.CreateConstraintAsync(new(id, "exists", "q"), fresh);
        await coordinator.CommitAsync(fresh);
        var reopened = GraphCatalog.Open(storage, coordinator);
        var verify = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        reopened.GetConstraints(verify.Snapshot).ShouldHaveSingleItem().PropertyKey.ShouldBe("q");
        await coordinator.RollbackAsync(verify);
    }

    private static MemoryStream Copy(MemoryStream source)
    {
        var copy = new MemoryStream();
        copy.Write(source.ToArray());
        copy.Position = 0;
        return copy;
    }
}
