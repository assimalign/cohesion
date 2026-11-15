using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Health;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

[Collection(DatabaseProvisioningHealthCollection.Name)]
public sealed class DatabaseJournalAdmissionHealthTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Component minimum matches HTTP across health conditions (#1790)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(31)]
    public async Task GetHealthAsync_WithConditionMatrix_ShouldMatchAggregateAndPreservePrecedence(int conditions)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var storage = new HealthStorage();
        await using var unavailable = new RecordingEngine("unavailable");
        await using var engine = new RecordingEngine("facts");
        await using var database = new HealthDatabase(engine);
        engine.Instances = [database];
        engine.ConfigureAdmission(database, storage);
        var worker = new RecordingEngineWorker("facts/worker", DatabaseEngineWorkerKind.IndexMaintenance, TimeSpan.FromHours(1));
        engine.AddWorker(worker);
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions { ReopenOfflineDatabases = false });
        builder.AddEngine(unavailable);
        builder.AddEngine(engine);
        await using var application = builder.Build(guard.Token);
        if ((conditions & 1) != 0) { await unavailable.DisposeAsync(); }
        if ((conditions & 2) != 0) { worker.Failure = new IOException("private path"); worker.RunIteration(guard.Token); }
        if ((conditions & 4) != 0) { engine.Offline = ["paused"]; }
        if ((conditions & 16) != 0) { database.HoldSchema(); }
        using var bracket = storage.BeginTransaction("OutsideTest");
        if ((conditions & 8) != 0)
        {
            storage.CheckpointJournalSize = 1;
            Assimalign.Cohesion.Database.Transactions.TransactionCheckpointInterlock.ForStorage(storage).ObserveAdmissionPause();
        }

        // Act
        var components = await application.Context.GetHealthAsync(guard.Token);
        var aggregate = await application.Context.CheckAsync(guard.Token);
        var single = await application.Context.GetHealthAsync("database/facts/paused", guard.Token);
        bracket.Rollback();

        // Assert
        components.Where(value => value.Name != "database.accepting").Min(value => (int)value.Status).ShouldBe((int)aggregate.Status);
        single.Status.ShouldBe(components.Single(value => value.Name == single.Name).Status);
        if ((conditions & 4) != 0)
        {
            single.Status.ShouldBe(DatabaseHealthStatus.Unhealthy);
            single.Description.ShouldNotBeNull().ShouldStartWith("Offline database:");
        }
        else if ((conditions & 8) != 0)
        {
            single.Status.ShouldBe(DatabaseHealthStatus.Degraded);
            single.Data["journalAdmissionPauseCode"].ShouldBe("CK06006");
            single.Description.ShouldNotBeNull().ShouldStartWith("CK06006:");
        }
        else if ((conditions & 16) != 0) { single.Status.ShouldBe(DatabaseHealthStatus.Degraded); }
        components.Single(value => value.Name == "engine/facts").Data.Keys.ShouldNotContain(key => key.StartsWith("provisioning.", StringComparison.Ordinal) || key.StartsWith("offline.", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Journal pause takes precedence over provisioning (#1790)")]
    public async Task GetHealthAsync_WithPausedProvisioning_ShouldKeepPauseCodeAndDescription()
    {
        // Arrange
        using var storage = new HealthStorage();
        await using var engine = new RecordingEngine("facts");
        await using var database = new HealthDatabase(engine);
        engine.Instances = [database];
        engine.ConfigureAdmission(database, storage);
        database.HoldSchema();
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build();
        using var bracket = storage.BeginTransaction("OutsideTest");
        storage.CheckpointJournalSize = 1;
        Assimalign.Cohesion.Database.Transactions.TransactionCheckpointInterlock.ForStorage(storage).ObserveAdmissionPause();

        // Act
        var component = await application.Context.GetHealthAsync("database/facts/paused", CancellationToken.None);
        var all = await application.Context.GetHealthAsync(CancellationToken.None);
        bracket.Rollback();

        // Assert
        component.Status.ShouldBe(DatabaseHealthStatus.Degraded);
        component.Data["journalAdmissionPauseCode"].ShouldBe("CK06006");
        component.Data["failureCode"].ShouldBe("CS03105");
        component.Description.ShouldNotBeNull().ShouldStartWith("CK06006:");
        all.Single(value => value.Name == component.Name).Description.ShouldBe(component.Description);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Journal admission degrades health online and restores itself after checkpoint (#1283)")]
    public async Task CheckAsync_JournalPaused_ShouldReportCodedDegradationAndRecovery()
    {
        // Arrange: real storage and admission attached through the root composition seam.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var storage = new HealthStorage();
        await using var engine = new RecordingEngine();
        await using var database = new HealthDatabase(engine);
        engine.ConfigureAdmission(database, storage);
        engine.Instances = [database];
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build(guard.Token);
        storage.CheckpointJournalSize = 1;
        var source = EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Hosting");
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        using (var outside = storage.BeginTransaction("OutsideTest"))
        {
            Assimalign.Cohesion.Database.Transactions.TransactionCheckpointInterlock.ForStorage(storage).ObserveAdmissionPause().ShouldBeTrue();
            // Act / Assert: no worker failure is needed to report the admission pause.
            var health = await application.Context.CheckAsync(guard.Token);
            health.Status.ShouldBe(HealthStatus.Degraded);
            health.Data.ShouldNotBeNull()["journalAdmissionPauseCode"].ShouldBe("CK06006");
            health.Data["journalAdmissionPausedDatabaseCount"].ShouldBe(1);
            engine.OfflineDatabases.ShouldBeEmpty();
            recorder.Events.ShouldContain(e => e.EventId == 16 && Equals(e.Payload![3], "JournalAdmissionPaused:CK06006"));
            outside.Rollback();
        }
        Assimalign.Cohesion.Database.Transactions.TransactionCheckpointInterlock.ForStorage(storage).TryCheckpoint(TimeSpan.Zero, guard.Token).ShouldBeTrue();
        (await application.Context.CheckAsync(guard.Token)).Status.ShouldBe(HealthStatus.Healthy);
        recorder.Events.ShouldContain(e => e.EventId == 17 && Equals(e.Payload![3], "JournalAdmissionPaused:CK06006"));
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Database close wakes a paused raw catalog caller (#1283)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_PausedRawCaller_ShouldWakeWithoutWaitingForCheckpoint(bool synchronous)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var storage = new HealthStorage();
        await using var engine = new RecordingEngine();
        await using var database = new HealthDatabase(engine);
        engine.ConfigureAdmission(database, storage);
        using (var bracket = storage.BeginTransaction()) { bracket.Commit(); }
        storage.CheckpointJournalSize = 1;
        var gate = Assimalign.Cohesion.Database.Transactions.TransactionCheckpointInterlock.ForStorage(storage);
        gate.ObserveAdmissionPause().ShouldBeTrue();
        var source = EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        var waiting = Task.Factory.StartNew(() =>
        { using var lease = gate.Enter("Catalog", cancellationToken: guard.Token); },
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        while (!recorder.Events.Any(e => e.EventId == 25)) { await Task.Delay(1, guard.Token); }
        // Act
        if (synchronous) { database.Dispose(); } else { await database.DisposeAsync(); }
        // Assert
        await waiting.WaitAsync(guard.Token);
        gate.IsAdmissionPaused.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - A storage safety fence wakes paused raw callers (#1283)")]
    public async Task TakeOffline_PausedRawCaller_ShouldWakeAndPreserveTheOwnersOfflineHook()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var storage = new HealthStorage();
        await using var engine = new RecordingEngine();
        await using var database = new HealthDatabase(engine);
        int notifications = 0; storage.OnOffline = _ => Interlocked.Increment(ref notifications);
        engine.ConfigureAdmission(database, storage);
        using (var bracket = storage.BeginTransaction()) { bracket.Commit(); }
        storage.CheckpointJournalSize = 1;
        var gate = Assimalign.Cohesion.Database.Transactions.TransactionCheckpointInterlock.ForStorage(storage);
        gate.ObserveAdmissionPause().ShouldBeTrue();
        var source = EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        var waiting = Task.Factory.StartNew(() =>
        { using var lease = gate.Enter("Catalog", cancellationToken: guard.Token); },
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        while (!recorder.Events.Any(e => e.EventId == 25)) { await Task.Delay(1, guard.Token); }
        // Act
        storage.TakeOffline(StorageOfflineCause.CheckpointFailures, "injected safety fence", new IOException("device refused"));
        // Assert
        (await Should.ThrowAsync<StorageOfflineException>(() => waiting.WaitAsync(guard.Token))).ShouldBeSameAs(storage.OfflineError);
        notifications.ShouldBe(1); gate.IsAdmissionPaused.ShouldBeFalse(); storage.IsOffline.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Admission registration cannot retain a failed unpublished database (#1283)")]
    public async Task ConfigureAdmission_UnpublishedConstructionFailure_ShouldReleaseTheWeakRegistration()
    {
        // Arrange: preserve the storage -> model callback cycle of a real engine.
        await using var engine = new RecordingEngine();
        WeakReference failed = RegisterUnpublished(engine);
        // Act
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        // Assert: the still-live engine cannot root the failed instance or its storages.
        failed.IsAlive.ShouldBeFalse();
        GC.KeepAlive(engine);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterUnpublished(RecordingEngine engine)
    {
        var storage = new HealthStorage();
        var database = new HealthDatabase(engine);
        storage.OnOffline = _ => GC.KeepAlive(database);
        engine.ConfigureAdmission(database, storage);
        storage.Dispose();
        return new WeakReference(database);
    }

    private sealed class HealthStorage : Storage.Storage
    {
        internal HealthStorage() : base(StorageModel.Sql, new StorageStream(new MemoryStream()),
            new StorageStream(new MemoryStream()), new StorageStream(new MemoryStream()))
        { ConfigureCommitDurability(StorageCommitDurability.None, "health-pause"); InitializeNew((Name)"health-pause"); }
    }

    private sealed class HealthDatabase : DatabaseInstance
    {
        internal HealthDatabase(DatabaseEngine engine) : base("paused", engine) { }
        internal void HoldSchema() => SetProvisioningProgress(new DatabaseProvisioningProgress("SchemaHeld", "items", 1, 1,
            false, failureCode: "CS03105", detail: "DropColumn 'items.Added' refused"));
        protected override ValueTask<DatabaseSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
        protected override void DisposeCore() { }
        protected override ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;
    }
}
