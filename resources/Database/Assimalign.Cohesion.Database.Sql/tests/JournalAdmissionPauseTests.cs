using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Tests;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class JournalAdmissionPauseTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Engine shutdown drains workers and wakes catalog callers during a pause (#1283)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_EnginePausedWithCatalogCaller_ShouldDrainBeforeClosingStorage(bool synchronous)
    {
        // Arrange: freeze maintenance BEFORE arming admission, so an initial purge
        // cannot already be waiting at data admission instead of our catalog hook.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var engine = SqlDatabaseEngine.Create("shutdown-pause", new()
        { CheckpointJournalSize = 0, CheckpointInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("paused", guard.Token);
        var maintenance = engine.Workers.OfType<Internal.SqlVersionPurgeWorker>().Single();
        using var releasePump = new ManualResetEventSlim();
        using var maintenanceEntered = new ManualResetEventSlim();
        var pumpEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observation = WorkerPumpObservation.Create(maintenance, _ => { }, _ =>
        { pumpEntered.TrySetResult(); releasePump.Wait(guard.Token); });
        Task writing = Task.CompletedTask;
        Task? disposal = null;
        try
        {
            while (!pumpEntered.Task.IsCompleted)
            { engine.UndoDeferredSignal.Set(); await Task.Delay(1, guard.Token); }
            await pumpEntered.Task;
            var worker = engine.Workers.OfType<Internal.SqlCheckpointWorker>().Single();
            worker.BeforeCheckpoint = _ => throw new System.IO.IOException("checkpoint worker refused");
            var dataGate = TransactionCheckpointInterlock.ForStorage(database.DataStorage);
            var catalogGate = TransactionCheckpointInterlock.ForStorage(database.CatalogStorage);
            using (var bracket = database.DataStorage.BeginTransaction()) { bracket.Commit(); }
            using (var bracket = database.CatalogStorage.BeginTransaction()) { bracket.Commit(); }
            dataGate.JournalSizeLimit = 2; catalogGate.JournalSizeLimit = 2;
            database.DataStorage.CheckpointJournalSize = 1; database.CatalogStorage.CheckpointJournalSize = 1;
            dataGate.IsAdmissionPaused.ShouldBeTrue(); catalogGate.IsAdmissionPaused.ShouldBeTrue();
            var source = EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Transactions");
            using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
            writing = Task.Factory.StartNew(() => database.Catalog.CreateSchemaAsync(
                new Catalog.SqlCatalogNamespace("during_shutdown"), guard.Token).GetAwaiter().GetResult(),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            while (!recorder.Events.Any(e => e.EventId == 25 && Equals(e.Payload![1], "SqlCatalog")))
            { await Task.Delay(1, guard.Token); }
            Thread? maintenanceThread = null;
            int maintenanceReads = 0;
            Action<SqlDatabase> readCatalog = current =>
            {
                maintenanceThread = Thread.CurrentThread; maintenanceEntered.Set();
                database.Catalog.TryGetTable("dbo", "missing", out _);
                Interlocked.Increment(ref maintenanceReads);
            };
            maintenance.BeforeFullPass = readCatalog; maintenance.BeforeDeferredUndoRetry = readCatalog;
            releasePump.Set();
            while (!maintenanceEntered.IsSet)
            { engine.UndoDeferredSignal.Set(); await Task.Delay(1, guard.Token); }
            while ((maintenanceThread!.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0)
            { await Task.Delay(1, guard.Token); }
            maintenanceReads.ShouldBe(0);
            // Act: engine close disables pausing before server disposal and worker joins.
            disposal = Task.Factory.StartNew(() =>
            {
                if (synchronous) { engine.Dispose(); }
                else { engine.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await disposal.WaitAsync(guard.Token);
            // Assert
            await writing.WaitAsync(guard.Token);
            engine.State.ShouldBe(EngineState.Disposed);
            Volatile.Read(ref maintenanceReads).ShouldBeGreaterThan(0);
            dataGate.IsAdmissionPaused.ShouldBeFalse(); catalogGate.IsAdmissionPaused.ShouldBeFalse();
        }
        finally
        {
            releasePump.Set();
            maintenance.BeforeFullPass = null; maintenance.BeforeDeferredUndoRetry = null;
            await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(45));
            if (disposal is not null) { await disposal.WaitAsync(TimeSpan.FromSeconds(45)); }
            try { await writing.WaitAsync(TimeSpan.FromSeconds(45)); }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException) { }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Catalog admission under its metadata monitor never blocks its checkpoint (#1283)")]
    public async Task Enter_CatalogMonitorDuringPause_ShouldResumeFromIndependentCheckpoint()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("catalog-pause", new()
        { CheckpointJournalSize = 0, CheckpointInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("paused", guard.Token);
        var gate = TransactionCheckpointInterlock.ForStorage(database.CatalogStorage);
        gate.JournalSizeLimit = 2; database.CatalogStorage.CheckpointJournalSize = 1;
        using var outside = database.CatalogStorage.BeginTransaction("OutsideTest");
        gate.IsAdmissionPaused.ShouldBeTrue();
        var source = EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        var writing = Task.Factory.StartNew(() => database.Catalog.CreateSchemaAsync(
            new Catalog.SqlCatalogNamespace("after_pause"), guard.Token).GetAwaiter().GetResult(),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            while (!recorder.Events.Any(e => e.EventId == 25)) { await Task.Delay(1, guard.Token); }
            // Act: the writer is paused under catalog _sync; checkpoint never asks for it.
            outside.Rollback();
            gate.TryCheckpoint(TimeSpan.Zero, guard.Token).ShouldBeTrue();
            await writing.WaitAsync(guard.Token);
            // Assert
            gate.IsAdmissionPaused.ShouldBeFalse();
            recorder.Events.ShouldContain(e => e.EventId == 25 && Equals(e.Payload![1], "SqlCatalog"));
        }
        finally
        {
            if (outside.IsActive) { outside.Rollback(); }
            gate.TryCheckpoint(TimeSpan.Zero, guard.Token);
            await writing.WaitAsync(guard.Token);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Journal admission distinguishes a statement deadline from caller cancellation (#1283)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_JournalPaused_ShouldServeReadsAndCodeOnlyStatementDeadlines(bool callerCancellation)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var caller = new CancellationTokenSource();
        await using var engine = SqlDatabaseEngine.Create("pause-timeout", new()
        { CheckpointJournalSize = 0, CheckpointInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("paused", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await using var reader = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("CREATE TABLE items(id INT)", cancellationToken: guard.Token);
        database.Coordinator.Checkpoint(guard.Token);
        var gate = TransactionCheckpointInterlock.ForStorage(database.DataStorage);
        gate.JournalSizeLimit = 2;
        database.DataStorage.CheckpointJournalSize = 1;
        using var outside = database.DataStorage.BeginTransaction("OutsideTest");
        gate.IsAdmissionPaused.ShouldBeTrue();
        session.StatementTimeout = TimeSpan.FromHours(1);
        var source = EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        var executing = session.ExecuteAsync("INSERT INTO items VALUES (1)", cancellationToken: caller.Token).AsTask();
        try
        {
            while (!recorder.Events.Any(e => e.EventId == 25)) { await Task.Delay(1, guard.Token); }
            // Act: reads never open a physical apply bracket, even while admission is paused.
            (await reader.ExecuteAsync("SELECT 1", cancellationToken: guard.Token)).Diagnostics.ShouldBeEmpty();
            if (callerCancellation) { caller.Cancel(); }
            else { session.ExpireStatementTimerForTest(); }
            Exception? failure = null;
            try { await executing; } catch (Exception exception) { failure = exception; }
            // Assert
            if (callerCancellation) { failure.ShouldBeAssignableTo<OperationCanceledException>(); }
            else
            {
                var timeout = failure.ShouldNotBeNull().ShouldBeOfType<SqlTimeoutException>();
                timeout.Diagnostics.ShouldContain(d => d.Code == "CK04014"); timeout.SqlState.ShouldBe("57014");
            }
            database.IsOffline.ShouldBeFalse(); gate.IsAdmissionPaused.ShouldBeTrue();
        }
        finally
        {
            caller.Cancel();
            try { await executing; } catch (Exception exception) when (exception is OperationCanceledException or DatabaseException) { }
            outside.Rollback();
            database.Coordinator.Checkpoint(guard.Token);
        }
        gate.IsAdmissionPaused.ShouldBeFalse();
        session.StatementTimeout = TimeSpan.Zero;
        (await session.ExecuteAsync("INSERT INTO items VALUES (2)", cancellationToken: guard.Token)).Diagnostics.ShouldBeEmpty();
    }
}
