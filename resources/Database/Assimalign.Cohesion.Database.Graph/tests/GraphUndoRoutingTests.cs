using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphUndoRoutingTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - Undo routing: equal entity/catalog formats keep entity rollback out of the catalog (#1806)")]
    public async Task RollbackAsync_EntityVersion_ShouldPreserveExactCatalogAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = GraphDatabaseEngine.Create("entity-undo-routing", new() { StorageStrategy = strategy,
            CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("source", token);
        await using var session = await database.CreateSessionAsync(token);
        await session.ExecuteAsync("CREATE LABEL Kept", cancellationToken: token);
        var before = await GraphCatalogIntrospectionTests.Rows(session, "SHOW LABELS");
        var catalogBefore = CatalogBytes(database);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        await session.ExecuteAsync("INSERT (:Kept)", cancellationToken: token);
        GraphRecordCodec.FormatVersion.ShouldBe(GraphCatalogCodec.FormatVersion);
        using (var entities = database.DataStorage.GetUnitIterator(2))
        { entities.MoveNext().ShouldBeTrue(); entities.MoveNext().ShouldBeFalse(); }
        int catalogDeletes = 0;
        var notify = database.DataStorage.CatalogVersionDeleted;
        database.DataStorage.CatalogVersionDeleted = (page, slot, bytes) => { catalogDeletes++; notify!(page, slot, bytes); };

        // Act
        try { await transaction.RollbackAsync(token); }
        finally { database.DataStorage.CatalogVersionDeleted = notify; }
        database.DataStorage.WriteBackDirtyPages(int.MaxValue);
        database.DataStorage.WriteAheadJournal.Flush(forceDurable: true);
        strategy.CopyDatabase("source", "image");

        // Assert
        catalogDeletes.ShouldBe(0);
        CatalogBytes(database).ShouldBe(catalogBefore);
        (await GraphCatalogIntrospectionTests.Rows(session, "SHOW LABELS")).SelectMany(row => row).ShouldBe(before.SelectMany(row => row));
        (await GraphCatalogIntrospectionTests.Rows(session, "MATCH (n) RETURN count(n)")).ShouldHaveSingleItem()[0].ShouldBe(0L);
        for (int reopen = 0; reopen < 2; reopen++)
        {
            var image = await engine.OpenDatabaseAsync("image", token);
            await using (var reader = await image.CreateSessionAsync(token))
            {
                var labels = await GraphCatalogIntrospectionTests.Rows(reader, "SHOW LABELS");
                labels.ShouldHaveSingleItem()[0].ShouldBe("image");
                labels.SelectMany(row => row.Skip(1)).ShouldBe(before.SelectMany(row => row.Skip(1)));
                CatalogBytes(image).ShouldBe(catalogBefore);
                (await GraphCatalogIntrospectionTests.Rows(reader, "MATCH (n) RETURN count(n)")).ShouldHaveSingleItem()[0].ShouldBe(0L);
            }
            await image.DisposeAsync();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Undo routing: deleting a catalog page's last slot unlinks it and recovers a live crash image (#1806)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackAsync_LastCatalogSlot_ShouldUnlinkAndRecoverExactStateAsync(bool crashDuringDelete)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = GraphDatabaseEngine.Create("catalog-undo-routing", new() { StorageStrategy = strategy,
            CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("source", token);
        await using var session = await database.CreateSessionAsync(token);
        var kept = await database.CreateNodeAsync(session, [], cancellationToken: token);
        var writer = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, token);
        var label = new GraphLabelMetadata(Guid.NewGuid(), "Transient");
        await database.Catalog.SaveLabelAsync(label, writer, token);
        GraphRecordCodec.FormatVersion.ShouldBe(GraphCatalogCodec.FormatVersion);
        using (var records = database.DataStorage.GetUnitIterator(0))
        {
            records.MoveNext().ShouldBeTrue();
            records.Current.Data.Span[17].ShouldBe(GraphRecordCodec.FormatVersion);
            records.MoveNext().ShouldBeFalse();
        }
        int deleted = 0, captured = 0;
        var notify = database.DataStorage.CatalogVersionDeleted;
        database.DataStorage.CatalogVersionDeleted = (page, slot, bytes) =>
        {
            deleted++;
            bytes.Span[16].ShouldBe((byte)1);
            bytes.Span[17].ShouldBe(GraphRecordCodec.FormatVersion);
            using (var handle = database.DataStorage.PageManager.GetPage(page))
            { handle.Page.Type.ShouldBe(PageType.Reclaimed); handle.Page.OwnerId.ShouldBe(0UL); }
            if (crashDuringDelete) { Capture(); }
            notify!(page, slot, bytes);
        };

        // Act
        try { await database.Coordinator.RollbackAsync(writer, token); }
        finally { database.DataStorage.CatalogVersionDeleted = notify; }
        if (!crashDuringDelete) { Capture(); }

        // Assert
        deleted.ShouldBe(1);
        captured.ShouldBe(1);
        var current = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, token);
        int catalogReads = 0;
        database.Catalog.BeforeRecordRead = () => catalogReads++;
        try
        {
            database.Catalog.FindLabel(label.Id, current.Snapshot).ShouldBeNull();
            database.Catalog.FindLabel(label.Name, current.Snapshot).ShouldBeNull();
            catalogReads.ShouldBe(0, "undo must unlink the reference before a lookup can lazily remove it");
        }
        finally { database.Catalog.BeforeRecordRead = null; await database.Coordinator.RollbackAsync(current, token); }
        for (int reopen = 0; reopen < 2; reopen++)
        {
            var image = await engine.OpenDatabaseAsync("image", token);
            await using (var reader = await image.CreateSessionAsync(token))
            {
                (await GraphCatalogIntrospectionTests.Rows(reader, "SHOW LABELS")).ShouldBeEmpty();
                CatalogBytes(image).ShouldBeEmpty();
                (await GraphCatalogIntrospectionTests.Rows(reader, "MATCH (n) RETURN count(n)")).ShouldHaveSingleItem()[0].ShouldBe(1L);
                var node = (await image.GetNodeAsync(reader, kept.Id, token)).ShouldNotBeNull();
                node.Labels.ShouldBeEmpty();
                node.Properties.ShouldBeEmpty();
            }
            await image.DisposeAsync();
        }

        void Capture()
        {
            database.DataStorage.WriteBackDirtyPages(int.MaxValue);
            database.DataStorage.WriteAheadJournal.Flush(forceDurable: true);
            strategy.CopyDatabase("source", "image");
            captured++;
        }
    }

    private static string[] CatalogBytes(GraphDatabase database)
    {
        var records = new List<string>();
        using var iterator = database.DataStorage.GetUnitIterator(0);
        while (iterator.MoveNext()) { records.Add(Convert.ToHexString(iterator.Current.Data.Span)); }
        return records.Order(StringComparer.Ordinal).ToArray();
    }
}
