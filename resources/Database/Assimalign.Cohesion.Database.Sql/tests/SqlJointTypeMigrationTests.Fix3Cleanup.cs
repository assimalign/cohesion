using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Worker rediscovers a republished reclamation incarnation (#1808)")]
    public async Task RunBatch_Fix3StaleIncarnation_ShouldRediscoverCurrentIntent()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = SqlDatabaseEngine.Create("stale-worker", new()
        { MaintenanceInterval = TimeSpan.FromHours(1), CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("CREATE TABLE t(v INT)", cancellationToken: guard.Token);
        var table = database.Catalog.CaptureSnapshot().Tables.Single();
        var runLock = typeof(SqlTableReclamation).GetField("_runLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(database.TableReclamation)!;
        // Keep the background worker outside this deterministic sequence of passes.
        lock (runLock)
        {
            ulong owner = database.Catalog.ReserveIndexTreeOwner(table.ObjectId, guard.Token);
            var old = database.Catalog.CaptureTableReclamations().Single(item => item.ObjectId == owner).Intent;
            using (var admission = TransactionCheckpointInterlock.ForStorage(database.DataStorage).Enter("SeedAbandonedTree", cancellationToken: guard.Token))
            using (var bracket = database.DataStorage.BeginTransaction())
            {
                using var page = database.DataStorage.AllocatePageForWrite(bracket, PageType.Index, ownerId: owner);
                page.MarkDirty();
                bracket.Commit();
            }
            database.Catalog.AbandonTableAs(owner);
            var pending = (Dictionary<ulong, SqlReclamationIntent>)typeof(SqlCatalog).GetField("_pendingReclamations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(database.Catalog)!;
            var sync = typeof(SqlCatalog).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(database.Catalog)!;
            var current = new SqlReclamationIntent(old.PageId, old.SlotIndex);
            int batches = 0;
            database.TableReclamation.BeforeBatch = (id, _) =>
            {
                id.ShouldBe(owner);
                batches++;
                if (batches != 1) { return; }
                // Replace the durable kind-14 receipt after capture. Inserting first
                // guarantees a different location as well as a new intent identity.
                lock (sync)
                {
                    using var admission = TransactionCheckpointInterlock.ForStorage(database.CatalogStorage)
                        .Enter("RepublishReclamation", cancellationToken: guard.Token, waitForAdmission: false);
                    using var bracket = database.CatalogStorage.BeginTransaction();
                    var location = database.CatalogStorage.InsertRow(bracket, owner, SqlReclamationRecord.Encode(owner, table.Schema, table.Name));
                    database.CatalogStorage.DeleteRow(bracket, old.PageId, old.SlotIndex);
                    bracket.Commit();
                    current = new SqlReclamationIntent(location.PageId, location.SlotIndex);
                    pending[owner] = current;
                }
                throw new InvalidOperationException("republished between worker passes");
            };

            // Act.
            bool interrupted = false;
            while (batches == 0)
            {
                guard.Token.ThrowIfCancellationRequested();
                try { database.TableReclamation.RunBatch(guard.Token); }
                catch (InvalidOperationException) when (batches == 1) { interrupted = true; }
                AdvanceReclamationHorizon(database, guard.Token);
            }
            interrupted.ShouldBeTrue();
            database.TableReclamation.RunBatch(guard.Token);
            // Assert: stale entry was dropped before any new batch; discovery is pending.
            batches.ShouldBe(1);
            database.TableReclamation.HasWork.ShouldBeTrue();
            database.Catalog.IsCurrentTableReclamation(owner, current).ShouldBeTrue();
            (current.PageId == old.PageId && current.SlotIndex == old.SlotIndex).ShouldBeFalse();
            database.DataStorage.GetOwnerIndexPages(owner).ShouldHaveSingleItem();
            while (database.TableReclamation.HasWork)
            {
                guard.Token.ThrowIfCancellationRequested();
                database.TableReclamation.RunBatch(guard.Token);
                AdvanceReclamationHorizon(database, guard.Token);
            }
            batches.ShouldBe(2);
            database.Catalog.IsCurrentTableReclamation(owner, current).ShouldBeFalse();
            database.DataStorage.GetOwnerIndexPages(owner).ShouldBeEmpty();
            database.TableReclamation.HasWork.ShouldBeFalse();
        }
    }

    // Both operations complete synchronously. Keep the worker monitor held across
    // these fence advances so no background pass can consume the staged incarnation.
    private static void AdvanceReclamationHorizon(SqlDatabase database, CancellationToken token)
    {
        var horizon = database.Coordinator.BeginSnapshotPinAsync(token).AsTask().GetAwaiter().GetResult();
        database.Coordinator.RollbackAsync(horizon, token).AsTask().GetAwaiter().GetResult();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint discard publishes retirement before lifecycle callbacks (#1808)")]
    public async Task DiscardJointIntentAsync_Fix3PreparedTree_ShouldPublishRetiringUnion()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("discard-union", new() { MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("CREATE TABLE t(v INT)", cancellationToken: guard.Token);
        var table = database.Catalog.CaptureSnapshot().Tables.Single();
        var pin = await database.Coordinator.BeginSnapshotPinAsync(guard.Token);
        ulong owner = database.Catalog.ReserveIndexTreeOwner(table.ObjectId, guard.Token);
        bool? retiring = null;
        database.Catalog.BeginIndexBuild(owner);
        database.Catalog.IndexBuildFinished += (id, _) => { if (id == owner) { retiring = database.Catalog.IsOwnerRetiring(id); } };
        // Act.
        await database.Catalog.DiscardJointIntentAsync(guard.Token, [table], [owner]);
        // Assert.
        retiring.ShouldBe(true);
        await database.Coordinator.RollbackAsync(pin, guard.Token);
        await DrainRequestedReclamationAsync(database, guard.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Prepared cleanup tolerates an absent directory owner (#1808)")]
    public async Task DiscardPrepared_Fix3MissingOwner_ShouldContinueCleanup()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("discard-missing", new() { MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("CREATE TABLE t(v INT)", cancellationToken: guard.Token);
        var table = database.Catalog.CaptureSnapshot().Tables.Single();
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, guard.Token);
        ulong owner = database.Catalog.ReserveIndexTreeOwner(table.ObjectId, guard.Token);
        try
        {
            await database.Coordinator.ApplyStatementAsync(context, async _ =>
            {
                await database.IndexManager.CreatePreparedAsync(context, table.ObjectId, owner, new IndexDefinition("ix"), guard.Token);
                return true;
            }, cancellationToken: guard.Token);
            var owners = (Dictionary<long, ulong>)typeof(SqlIndexManager).GetField("_owners", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(database.IndexManager)!;
            owners.Clear();
            // Act / Assert.
            Should.NotThrow(() => database.IndexManager.DiscardPrepared());
            database.IndexManager.TryGetPrepared(table.ObjectId, "ix", out _).ShouldBeFalse();
        }
        finally
        {
            database.Catalog.AbandonTableAs(owner);
            await database.Coordinator.RollbackAsync(context, guard.Token);
        }
        await DrainRequestedReclamationAsync(database, guard.Token);
    }
}
