using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed class CheckpointBracketInterlockTests
{
    private readonly ITestOutputHelper _output;
    public CheckpointBracketInterlockTests(ITestOutputHelper output) { _output = output; }
    [Fact(DisplayName = "Cohesion Test [Sql] - Reclamation rechecks checkpoint priority after waiting for writer admission (#1283)")]
    public async Task RunBatch_TriggerCrossedWhileWaitingForAdmission_ShouldYieldBeforeOpeningABracket()
    {
        // Arrange: a real retired owner, with reclamation paused before its first due check.
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var allowBatch = new ManualResetEventSlim();
        using var holderEntered = new ManualResetEventSlim();
        using var write = new ManualResetEventSlim();
        using var written = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var beforeBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beforeAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = SqlDatabaseEngine.Create("reclamation-admission", new()
        {
            CheckpointJournalSize = 0,
            CheckpointInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("late-trigger", ceiling.Token);
        await using var setup = await database.CreateSessionAsync(ceiling.Token);
        await setup.ExecuteAsync("CREATE TABLE retired (id INT)", cancellationToken: ceiling.Token);
        await setup.ExecuteAsync("INSERT INTO retired VALUES (1)", cancellationToken: ceiling.Token);
        Thread? reclamationThread = null;
        int visits = 0, commits = 0;
        database.TableReclamation.BeforeBatch = (_, token) =>
        {
            if (Interlocked.Increment(ref visits) != 1) { return; }
            reclamationThread = Thread.CurrentThread;
            beforeBatch.TrySetResult();
            allowBatch.Wait(token);
            beforeAdmission.TrySetResult();
        };
        database.TableReclamation.BeforeBatchCommit = (_, _) => Interlocked.Increment(ref commits);
        Action? original = database.DataStorage.OnCheckpointNeeded;
        Task? reclaiming = null;
        Task? holder = null;
        long reclaimed = database.TableReclamation.ReclaimedPages;
        try
        {
            await setup.ExecuteAsync("DROP TABLE retired", cancellationToken: ceiling.Token);
            reclaiming = Task.Factory.StartNew(() => database.TableReclamation.RunBatch(ceiling.Token),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await beforeBatch.Task.WaitAsync(ceiling.Token);
            database.Coordinator.Checkpoint(ceiling.Token);
            database.DataStorage.OnCheckpointNeeded = () => { }; // Keep the due request ahead of the worker's first attempt.
            holder = Task.Factory.StartNew(() =>
            {
                using var scope = TransactionCheckpointInterlock.ForStorage(database.DataStorage).Enter("Statement", cancellationToken: ceiling.Token);
                holderEntered.Set();
                write.Wait(ceiling.Token);
                using (var bracket = database.DataStorage.BeginTransaction("Statement")) { bracket.Commit(); }
                written.Set();
                release.Wait(ceiling.Token);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            holderEntered.Wait(ceiling.Token);
            allowBatch.Set();
            await beforeAdmission.Task.WaitAsync(ceiling.Token);
            while ((reclamationThread!.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0)
            { await Task.Delay(1, ceiling.Token); }

            // Act: reclamation already passed the earlier check and now waits behind this writer.
            database.DataStorage.CheckpointJournalSize = 1;
            write.Set();
            written.Wait(ceiling.Token);
            database.DataStorage.IsCheckpointDue(TimeSpan.FromHours(1)).ShouldBeTrue();
            release.Set();
            await Task.WhenAll(holder, reclaiming).WaitAsync(ceiling.Token);

            // Assert: no reclamation bracket opened; its early exit also released the gate.
            Volatile.Read(ref commits).ShouldBe(0);
            database.TableReclamation.ReclaimedPages.ShouldBe(reclaimed);
            database.TableReclamation.BeforeBatch = null;
            database.DataStorage.CheckpointJournalSize = 0;
            database.Coordinator.Checkpoint(ceiling.Token);
            database.TableReclamation.RunBatch(ceiling.Token);
            Volatile.Read(ref commits).ShouldBeGreaterThan(0);
            database.TableReclamation.ReclaimedPages.ShouldBeGreaterThan(reclaimed);
        }
        finally
        {
            allowBatch.Set(); write.Set(); release.Set();
            try { await Task.WhenAll(holder ?? Task.CompletedTask, reclaiming ?? Task.CompletedTask).WaitAsync(ceiling.Token); }
            finally
            {
                database.TableReclamation.BeforeBatch = null;
                database.TableReclamation.BeforeBatchCommit = null;
                database.DataStorage.CheckpointJournalSize = 0;
                database.DataStorage.OnCheckpointNeeded = original;
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Checkpoint interlock: catalog, reclamation and statements overlap without lock inversion (#1283)")]
    public async Task TryCheckpoint_CatalogReclamationAndStatements_ShouldExerciseEveryHandoff()
    {
        // Arrange: force overlap at real journal appends and real reclamation commits.
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var engine = SqlDatabaseEngine.Create("bracket-stress", new()
        {
            CheckpointJournalSize = 0,
            CheckpointInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("stress", ceiling.Token);
        await using var setup = await database.CreateSessionAsync(ceiling.Token);
        await using var writer = await database.CreateSessionAsync(ceiling.Token);
        await setup.ExecuteAsync("CREATE TABLE live (id INT)", cancellationToken: ceiling.Token);
        var catalogGate = TransactionCheckpointInterlock.ForStorage(database.CatalogStorage);
        var source = EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        Action? original = database.CatalogStorage.OnCheckpointNeeded;
        int rounds = 0;
        int deferred = 0;
        var watch = Stopwatch.StartNew();
        try
        {
            do
            {
                using var reclaimEntered = new ManualResetEventSlim();
                using var catalogEntered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                await setup.ExecuteAsync($"CREATE TABLE retired{rounds} (id INT)", cancellationToken: ceiling.Token);
                await setup.ExecuteAsync($"INSERT INTO retired{rounds} VALUES (1)", cancellationToken: ceiling.Token);
                database.TableReclamation.BeforeBatchCommit = (_, token) =>
                { reclaimEntered.Set(); release.Wait(token); };
                await setup.ExecuteAsync($"DROP TABLE retired{rounds}", cancellationToken: ceiling.Token);
                catalogGate.TryCheckpoint(TimeSpan.Zero, ceiling.Token).ShouldBeTrue();
                database.CatalogStorage.CheckpointJournalSize = 1;
                database.CatalogStorage.OnCheckpointNeeded = () =>
                { catalogEntered.Set(); release.Wait(ceiling.Token); };
                var reclaiming = Task.Factory.StartNew(() => database.TableReclamation.RunBatch(ceiling.Token),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Task? catalog = null;
                Task? writing = null;
                try
                {
                    reclaimEntered.Wait(ceiling.Token);
                    catalog = Task.Factory.StartNew(() => database.Catalog.CreateSchemaAsync(
                        new SqlCatalogNamespace($"scope{rounds}"), ceiling.Token).GetAwaiter().GetResult(),
                        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    catalogEntered.Wait(ceiling.Token);
                    // Act: the statement queues behind reclamation, while a separate catalog
                    // bracket holds its own gate. Neither checkpoint waits for a model lock.
                    writing = Task.Run(async () => await writer.ExecuteAsync("INSERT INTO live VALUES (1)", cancellationToken: ceiling.Token));
                    database.Coordinator.TryCheckpoint(TimeSpan.Zero, ceiling.Token).ShouldBeFalse();
                    catalogGate.TryCheckpoint(TimeSpan.Zero, ceiling.Token).ShouldBeFalse();
                    deferred += 2;
                }
                finally
                {
                    release.Set();
                    try
                    {
                        await Task.WhenAll(reclaiming, catalog ?? Task.CompletedTask, writing ?? Task.CompletedTask).WaitAsync(ceiling.Token);
                    }
                    catch
                    {
                        _output.WriteLine($"round {rounds}: reclaim={reclaiming.Status} {reclaiming.Exception}; catalog={catalog?.Status} {catalog?.Exception}; statement={writing?.Status} {writing?.Exception}");
                        throw;
                    }
                    finally
                    {
                        database.CatalogStorage.CheckpointJournalSize = 0;
                        database.CatalogStorage.OnCheckpointNeeded = original;
                        database.TableReclamation.BeforeBatchCommit = null;
                    }
                }
                rounds++;
                ceiling.Token.ThrowIfCancellationRequested();
            }
            while (watch.Elapsed < TimeSpan.FromMilliseconds(250) || rounds < 8);
        }
        finally
        {
            database.TableReclamation.BeforeBatchCommit = null;
            database.CatalogStorage.CheckpointJournalSize = 0;
            database.CatalogStorage.OnCheckpointNeeded = original;
        }
        // Assert: independent counters prove the overlap, reclamation and both attributions.
        _output.WriteLine($"rounds={rounds}, forced deferrals={deferred}, reclaimed pages={database.TableReclamation.ReclaimedPages}");
        rounds.ShouldBeGreaterThanOrEqualTo(8);
        deferred.ShouldBe(rounds * 2);
        database.TableReclamation.ReclaimedPages.ShouldBeGreaterThanOrEqualTo(rounds);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        var attributed = recorder.Events.Where(e => e.EventId == 21).ToArray();
        attributed.Count(e => Equals(e.Payload![1], "SqlCatalog")).ShouldBeGreaterThanOrEqualTo(rounds);
        attributed.Count(e => Equals(e.Payload![1], "SqlTableReclamation")).ShouldBeGreaterThanOrEqualTo(rounds);
    }
}
