using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Tests;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class JournalAdmissionPauseTests
{
    private sealed record PendingRow(int Id);

    internal static async Task ReadRowsAsync(SqlDatabaseSession session, string sql, int expectedCount, CancellationToken token)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: token)).ShouldBeAssignableTo<DatabaseResultSet>();
        result.Diagnostics.ShouldBeEmpty();
        int count = 0;
        await foreach (var row in result.GetRowsAsync(token))
        {
            row.GetValue(0).ShouldBe(1);
            count++;
        }
        count.ShouldBe(expectedCount);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Refused publication releases the snapshot gate for BEGIN and a table read (#1283)")]
    public async Task ExecuteAsync_CatalogPausedAtPublication_ShouldReleaseSnapshotGateBeforeBegin()
    {
        // Arrange: reach the real TRUNCATE publication after preparing its replacement.
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var engine = SqlDatabaseEngine.Create("publication-pause", new()
        { CheckpointJournalSize = 0, CheckpointInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("db", ceiling.Token);
        await using var writer = await database.CreateSessionAsync(ceiling.Token);
        await using var reader = await database.CreateSessionAsync(ceiling.Token);
        await writer.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: ceiling.Token);
        await writer.ExecuteAsync("INSERT INTO t VALUES(1)", cancellationToken: ceiling.Token);
        var gate = TransactionCheckpointInterlock.ForStorage(database.CatalogStorage);
        var worker = engine.Workers.OfType<Internal.SqlCheckpointWorker>().Single();
        worker.BeforeCheckpoint = _ => throw new StorageTransactionException("checkpoint deliberately pending");
        Internal.SqlPlanExecutor.TruncatePrepared = () =>
        {
            database.CatalogStorage.CheckpointJournalSize = 1;
            gate.JournalSizeLimit = 2;
            gate.ObserveAdmissionPause().ShouldBeTrue();
        };
        try
        {
            // Act: publication owns TruncateSnapshotGate, so it must refuse a pause.
            var refusal = await Should.ThrowAsync<SqlTimeoutException>(() => writer.ExecuteAsync(
                "TRUNCATE TABLE t", cancellationToken: ceiling.Token).AsTask());
            // Assert: the failed publication releases its marker, gate and DDL lease.
            refusal.Diagnostics.ShouldContain(d => d.Code == "CK04014");
            gate.IsAdmissionPaused.ShouldBeTrue(); ceiling.IsCancellationRequested.ShouldBeFalse();
            await reader.ExecuteAsync("BEGIN", cancellationToken: ceiling.Token);
            await ReadRowsAsync(reader, "SELECT * FROM t", 1, ceiling.Token);
            await reader.ExecuteAsync("ROLLBACK", cancellationToken: ceiling.Token);
        }
        finally
        {
            Internal.SqlPlanExecutor.TruncatePrepared = null;
            worker.BeforeCheckpoint = null; gate.JournalSizeLimit = 0;
            database.CatalogStorage.CheckpointJournalSize = 0;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Reopen discards a pending joint intent before arming recovered catalog admission (#1283)")]
    public async Task OpenDatabaseAsync_PendingIntentAndRecoveredCatalogPastHalfLimit_ShouldCompleteBeforeWorkerPublication()
    {
        // Arrange: retain crash images before orderly disposal truncates the journal.
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var strategy = new TestObjects.CrashCaptureSqlStorageStrategy();
        TestObjects.CrashCaptureSqlStorageStrategy crash;
        await using (var seed = SqlDatabaseEngine.Create("seed-pending", new()
        { StorageStrategy = strategy, CheckpointJournalSize = 0, CheckpointInterval = TimeSpan.FromHours(1) }))
        {
            var database = await seed.CreateDatabaseAsync("pending", ceiling.Token);
            var target = Schema.SqlSchema.Create("pending", schema =>
                schema.Table<PendingRow>("future", table => table.Key(row => row.Id))).Compile();
            await database.Catalog.SavePendingMigrationAsync(new Catalog.SqlCatalogPendingMigration(
                target.Hash, target.CanonicalDocument, [new("Joint/AddTable", "future", "future", 1)], 1), ceiling.Token);
            database.CatalogStorage.JournalBytesSinceCheckpoint.ShouldBeGreaterThan(512);
            crash = strategy.CaptureDurableImages();
        }
        await using var engine = SqlDatabaseEngine.Create("reopen-pending", new()
        { StorageStrategy = crash, CheckpointJournalSize = 512, JournalSizeLimit = 1024, CheckpointInterval = TimeSpan.FromHours(1) });
        // Act: there is no published instance for a worker to resume during this open.
        var reopened = await engine.OpenDatabaseAsync("pending", ceiling.Token);
        // Assert
        reopened.Catalog.PendingMigration.ShouldBeNull();
        reopened.IsOffline.ShouldBeFalse();
        (await engine.CreateDatabaseAsync("unrelated", ceiling.Token)).IsOffline.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Catalog pause leaves scans, seeks and BEGIN available while an identity INSERT waits (#1283)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecuteAsync_CatalogPausedIdentityReservation_ShouldLeaveTableReadsAvailable(int ending)
    {
        // Arrange
        bool shutdown = ending != 0;
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var engine = SqlDatabaseEngine.Create("catalog-pause", new()
        { CheckpointJournalSize = 0, CheckpointInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("paused", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await using var reader = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY)", cancellationToken: guard.Token);
        await writer.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: guard.Token);
        await writer.ExecuteAsync("CREATE TABLE identities(id INT IDENTITY(1,1), value INT)", cancellationToken: guard.Token);
        var worker = engine.Workers.OfType<Internal.SqlCheckpointWorker>().Single();
        worker.BeforeCheckpoint = _ => throw new System.IO.IOException("checkpoint deliberately pending");
        var gate = TransactionCheckpointInterlock.ForStorage(database.CatalogStorage);
        using (var bracket = database.CatalogStorage.BeginTransaction()) { bracket.Commit(); }
        database.CatalogStorage.CheckpointJournalSize = 1;
        gate.JournalSizeLimit = 2;
        gate.ObserveAdmissionPause().ShouldBeTrue();
        var source = EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        var writing = Task.Factory.StartNew(() => writer.ExecuteAsync("INSERT INTO identities(value) VALUES (9)",
            cancellationToken: guard.Token).AsTask().GetAwaiter().GetResult(), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            while (!recorder.Events.Any(e => e.EventId == 25)) { await Task.Delay(1, guard.Token); }
            // Act: all of these bind a real table through _sync while the writer waits.
            await ReadRowsAsync(reader, "SELECT * FROM t", 1, guard.Token);
            reader.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("scan");
            await ReadRowsAsync(reader, "SELECT * FROM t WHERE id = 1", 1, guard.Token);
            reader.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldStartWith("seek:");
            await reader.ExecuteAsync("BEGIN", cancellationToken: guard.Token);
            await ReadRowsAsync(reader, "SELECT * FROM t", 1, guard.Token);
            await reader.ExecuteAsync("ROLLBACK", cancellationToken: guard.Token);
            writing.IsCompleted.ShouldBeFalse();
            // A metadata mutation retains validated metadata and refuses without parking.
            await Should.ThrowAsync<TransactionAdmissionCanceledException>(() => database.Catalog.CreateSchemaAsync(
                new Catalog.SqlCatalogNamespace("refused"), guard.Token).AsTask());
            if (ending == 2) { engine.Dispose(); }
            else if (shutdown) { await engine.DisposeAsync().AsTask().WaitAsync(guard.Token); }
            else { gate.TryCheckpoint(TimeSpan.Zero, guard.Token).ShouldBeTrue(); }
            try { await writing.WaitAsync(guard.Token); }
            catch (Exception exception) when (shutdown && exception is ObjectDisposedException or OperationCanceledException or DatabaseTransactionAbortedException) { }
            // Assert
            gate.IsAdmissionPaused.ShouldBeFalse();
            if (shutdown) { engine.State.ShouldBe(EngineState.Disposed); }
            recorder.Events.ShouldNotContain(e => e.EventId == 0);
        }
        finally
        {
            worker.BeforeCheckpoint = null;
            gate.JournalSizeLimit = 0;
            try { await writing.WaitAsync(guard.Token); }
            catch (Exception exception) when (shutdown && exception is ObjectDisposedException or OperationCanceledException or DatabaseTransactionAbortedException) { }
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
        gate.ObserveAdmissionPause().ShouldBeTrue();
        session.StatementTimeout = TimeSpan.FromHours(1);
        var source = EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        var executing = session.ExecuteAsync("INSERT INTO items VALUES (1)", cancellationToken: caller.Token).AsTask();
        try
        {
            while (!recorder.Events.Any(e => e.EventId == 25)) { await Task.Delay(1, guard.Token); }
            // Act: reads never open a physical apply bracket, even while admission is paused.
            await ReadRowsAsync(reader, "SELECT * FROM items", 0, guard.Token);
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
            database.IsOffline.ShouldBeFalse(); gate.ObserveAdmissionPause().ShouldBeTrue();
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
