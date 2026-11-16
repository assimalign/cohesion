using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Constraint roots after statement admission wait through a checkpoint pause (#1808)")]
    public async Task ExecuteAsync_PauseAfterFirstConstraintRoot_ShouldWaitAndPublishBoth()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = SqlDatabaseEngine.Create("constraint-roots", new()
        { CheckpointJournalSize = 0, MaintenanceInterval = TimeSpan.FromHours(1), CheckpointInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("CREATE TABLE seed(v INT)", cancellationToken: guard.Token);
        var gate = TransactionCheckpointInterlock.ForStorage(database.DataStorage);
        using var holder = database.DataStorage.BeginTransaction("HoldConstraintCheckpoint");
        using var events = new EventSourceRecorder(EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Transactions"), EventLevel.Verbose);
        int roots = 0;
        SqlPlanExecutor.IndexBuildBoundary = (stage, _, _) =>
        {
            if (stage != "root-before-commit" || ++roots != 1) { return; }
            database.DataStorage.CheckpointJournalSize = 1;
            gate.JournalSizeLimit = 2;
            gate.ObserveAdmissionPause().ShouldBeTrue();
        };
        var creating = Task.Run(async () => await writer.ExecuteAsync("CREATE TABLE t(a INT PRIMARY KEY, b INT UNIQUE)", cancellationToken: guard.Token), guard.Token);
        try
        {
            // Act.
            while (!events.Events.Any(item => item.EventId == 25 && Equals(item.Payload![0], database.DataStorage.Name.ToString())))
            { if (creating.IsCompleted) { await creating; } await Task.Delay(1, guard.Token); }
            creating.IsCompleted.ShouldBeFalse();
            gate.JournalSizeLimit = 0; database.DataStorage.CheckpointJournalSize = 0; holder.Rollback();
            database.Coordinator.Checkpoint(guard.Token);
            await creating.WaitAsync(guard.Token);

            // Assert.
            roots.ShouldBe(2);
            database.Catalog.GetIndexRegistrations().Count.ShouldBe(2);
            database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        }
        finally
        {
            SqlPlanExecutor.IndexBuildBoundary = null;
            gate.JournalSizeLimit = 0; database.DataStorage.CheckpointJournalSize = 0; holder.Dispose();
            database.Coordinator.Checkpoint(guard.Token);
            try { await creating.WaitAsync(guard.Token); } catch { /* Preserve the test's original failure. */ }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Index prune reads retirement without waiting on the catalog monitor (#1808)")]
    public async Task PruneAsync_CatalogMonitorHeld_ShouldReadPublishedRetirementSet()
    {
        // Arrange: use a 30-second hang guard, never a timing assertion.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("prune-catalog", new() { MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("CREATE TABLE t(a INT PRIMARY KEY)", cancellationToken: guard.Token);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => { lock (CatalogMonitor(database.Catalog)) { entered.Set(); release.Wait(); } });
        entered.Wait(guard.Token);
        try
        {
            // Act.
            await Task.Run(async () => await database.IndexManager.PruneAsync(database.Coordinator.PruneBound, guard.Token), CancellationToken.None).WaitAsync(guard.Token);

            // Assert.
            database.IndexManager.ExportRegistrations().ShouldHaveSingleItem();
        }
        finally { release.Set(); await holder; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Completed joint builds abandoned before publication emit one unsuccessful end (#1808)")]
    public async Task ApplyAsync_AbandonPreparedReplacement_ShouldEndEveryBuildOnce()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = await Builder(new()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        SqlCompiledSchema Declare(bool target) => Compiled(new CompiledSchemaTable("items", "row",
            [new("v", DatabaseType.Int32, false), new("w", DatabaseType.Int32, true)], new("pk", ["v"]),
            [new("ix", target ? ["w", "v"] : ["w"], false)], []));
        await database.ApplySchemaAsync(Declare(false), guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(1,1)", cancellationToken: guard.Token);
        using var events = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Informational);
        database.SchemaProvisioner.JointStage = stage => { if (stage == "Publishing") { throw new InvalidOperationException("injected publication refusal"); } };

        // Act.
        try { await database.SchemaProvisioner.ApplyAsync(Declare(true), guard.Token, deferDataScalingSteps: true); await WaitDoneAsync(database, guard.Token); }
        finally { database.SchemaProvisioner.JointStage = null; }

        // Assert.
        database.Provisioning!.FailureCode.ShouldBe("CS03402");
        var began = events.Events.Where(item => item.EventId == 28).ToArray();
        began.ShouldNotBeEmpty();
        foreach (var start in began)
        {
            var ended = events.Events.Where(item => item.EventId == 29 && Equals(item.Payload![2], start.Payload![2])).ShouldHaveSingleItem();
            ended.Payload![3].ShouldBe(false);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Reclamation rejects a stale intent instance for the same owner (#1808)")]
    public async Task IsCurrentTableReclamation_StaleIntent_ShouldRejectWithoutRemovingCurrent()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("stale-intent", new() { MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("CREATE TABLE t(v INT)", cancellationToken: guard.Token);
        var table = database.Catalog.CaptureSnapshot().Tables.Single();
        ulong owner = database.Catalog.ReserveIndexTreeOwner(table.ObjectId, guard.Token);
        var current = database.Catalog.CaptureTableReclamations().Single(item => item.ObjectId == owner).Intent;
        var stale = new SqlReclamationIntent(current.PageId, current.SlotIndex);

        // Act.
        bool accepted = database.Catalog.IsCurrentTableReclamation(owner, stale);
        await database.Catalog.CompleteTableReclamationAsync(owner, stale, guard.Token);

        // Assert.
        accepted.ShouldBeFalse();
        database.Catalog.IsCurrentTableReclamation(owner, current).ShouldBeTrue();
        database.Catalog.AbandonTableAs(owner);
        await DrainRequestedReclamationAsync(database, guard.Token);
        database.Catalog.IsCurrentTableReclamation(owner, current).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Session index owner reservations retain the caller's lock timeout (#1808)")]
    public async Task ExecuteAsync_CatalogReservationPaused_ShouldApplyLockTimeout()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("reservation-timeout", new()
        { LockTimeout = TimeSpan.FromMilliseconds(100), CheckpointJournalSize = 0, CheckpointInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("CREATE TABLE t(v INT)", cancellationToken: guard.Token);
        var gate = TransactionCheckpointInterlock.ForStorage(database.CatalogStorage);
        using var holder = database.CatalogStorage.BeginTransaction("HoldReservationCheckpoint");
        gate.JournalSizeLimit = 2;
        gate.ObserveAdmissionPause().ShouldBeTrue();
        try
        {
            // Act.
            var error = await Should.ThrowAsync<SqlTimeoutException>(async () => await writer.ExecuteAsync("CREATE INDEX ix ON t(v)", cancellationToken: guard.Token));

            // Assert.
            error.Diagnostics.ShouldContain(item => item.Code == "CK04014");
            guard.IsCancellationRequested.ShouldBeFalse();
        }
        finally { gate.JournalSizeLimit = 0; holder.Dispose(); database.CatalogStorage.Checkpoint(); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Index callbacks observe complete catalog publications (#1808)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateIndexAsync_PublicationCallbacks_ShouldObserveCompleteMetadata(bool buildCallback)
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("postcommit-callback", new() { MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("CREATE TABLE t(v INT)", cancellationToken: guard.Token);
        var table = database.Catalog.CaptureSnapshot().Tables.Single();
        int completed = 0, retired = 0;
        var original = database.Catalog.IndexBuildFinished;
        if (buildCallback) { database.Catalog.IndexBuildFinished = (owner, succeeded) =>
        {
            if (succeeded) { database.Catalog.GetIndexes(table.ObjectId).Select(index => index.Name).ShouldContain("ix"); completed++; }
            original?.Invoke(owner, succeeded);
        }; }

        // Act.
        await writer.ExecuteAsync("CREATE INDEX ix ON t(v)", cancellationToken: guard.Token);
        database.Catalog.IndexReclamationRequested = () =>
        { database.Catalog.GetIndexes(table.ObjectId).ShouldBeEmpty(); retired++; };
        await database.Catalog.DropIndexAsync(table.ObjectId, "ix", [], guard.Token);

        // Assert.
        completed.ShouldBe(buildCallback ? 1 : 0);
        retired.ShouldBe(1);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_sync")]
    private static extern ref object CatalogMonitor(SqlCatalog catalog);
}
