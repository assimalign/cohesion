using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class CheckpointBracketInterlockTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Pending catalog checkpoint runs from a catalog write inside data apply (#1283)")]
    public async Task ApplyStatement_CatalogCheckpointPending_ShouldDisposeBothGatesInOrder()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("nested-catalog", new()
        { CheckpointJournalSize = 0, CheckpointInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("nested", guard.Token);
        var context = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, guard.Token);
        var catalogGate = TransactionCheckpointInterlock.ForStorage(database.CatalogStorage);
        var source = EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new Assimalign.Cohesion.Testing.EventSourceRecorder(source, EventLevel.Verbose);
        database.CatalogStorage.CheckpointJournalSize = 1;
        Action? original = database.CatalogStorage.OnCheckpointNeeded;
        database.CatalogStorage.OnCheckpointNeeded = () => { entered.Set(); release.Wait(guard.Token); };
        var applying = Task.Factory.StartNew(() => database.Coordinator.ApplyStatementAsync(context, _ =>
        {
            database.Catalog.CreateSchemaAsync(new SqlCatalogNamespace("inside_apply"), guard.Token).GetAwaiter().GetResult();
            return 0;
        }, guard.Token).AsTask().GetAwaiter().GetResult(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            entered.Wait(guard.Token);
            // Act: the data holder is inside the catalog monitor and its independent gate.
            catalogGate.TryCheckpoint(TimeSpan.Zero, guard.Token).ShouldBeFalse();
            release.Set();
            await applying.WaitAsync(guard.Token);
            // Assert: disposal flushed the catalog before unwinding data admission.
            database.CatalogStorage.IsCheckpointDue(TimeSpan.MaxValue).ShouldBeFalse();
            recorder.Events.ShouldContain(e => e.EventId == 21 && Equals(e.Payload![1], "SqlCatalog"));
            database.Coordinator.TryCheckpoint(TimeSpan.Zero, guard.Token).ShouldBeTrue();
            await database.Coordinator.CommitAsync(context, guard.Token);
        }
        finally
        {
            release.Set();
            await applying.WaitAsync(guard.Token);
            database.CatalogStorage.OnCheckpointNeeded = original;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Free-running catalog, statement, reclamation and checkpoint contention (#1283)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryCheckpoint_FreeRunningInterleavings_ShouldExerciseContentionAndDrain(bool cancelAtCeiling)
    {
        // Arrange: no per-iteration barriers or forced handoff. Duration is a minimum;
        // counters decide completion and the ceiling only diagnoses a stuck workload.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var engine = SqlDatabaseEngine.Create("free-interleaving", new()
        { CheckpointJournalSize = 65536, CheckpointInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("stress", guard.Token);
        await using var setup = await database.CreateSessionAsync(guard.Token);
        await setup.ExecuteAsync("CREATE TABLE live(v INT)", cancellationToken: guard.Token);
        var source = EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new Assimalign.Cohesion.Testing.EventSourceRecorder(source, EventLevel.Verbose);
        var catalogGate = TransactionCheckpointInterlock.ForStorage(database.CatalogStorage);
        await using var firstWriter = await database.CreateSessionAsync(guard.Token);
        await using var secondWriter = await database.CreateSessionAsync(guard.Token);
        await using var retiringSession = await database.CreateSessionAsync(guard.Token);
        var sessions = new[] { firstWriter, secondWriter };
        long[] writes = new long[2];
        int schemas = 0, retired = 0, dataDeferrals = 0, catalogDeferrals = 0, stop = 0;
        Task Actor(Action work) => Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var writers = Enumerable.Range(0, 2).Select(index => Actor(() =>
        {
            var session = sessions[index];
            while (Volatile.Read(ref stop) == 0)
            {
                session.ExecuteAsync("INSERT INTO live VALUES(1)", cancellationToken: guard.Token).GetAwaiter().GetResult().Diagnostics.ShouldBeEmpty();
                Interlocked.Increment(ref writes[index]);
            }
        })).ToArray();
        var catalog = Actor(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                int index = Interlocked.Increment(ref schemas);
                database.Catalog.CreateSchemaAsync(new SqlCatalogNamespace($"scope{index}"), guard.Token).GetAwaiter().GetResult();
                Thread.Yield();
            }
        });
        var reclamation = Actor(() =>
        {
            var session = retiringSession;
            while (Volatile.Read(ref stop) == 0)
            {
                int index = Interlocked.Increment(ref retired);
                session.ExecuteAsync($"CREATE TABLE retired{index}(v INT)", cancellationToken: guard.Token).GetAwaiter().GetResult().Diagnostics.ShouldBeEmpty();
                session.ExecuteAsync($"INSERT INTO retired{index} VALUES(1)", cancellationToken: guard.Token).GetAwaiter().GetResult().Diagnostics.ShouldBeEmpty();
                session.ExecuteAsync($"DROP TABLE retired{index}", cancellationToken: guard.Token).GetAwaiter().GetResult().Diagnostics.ShouldBeEmpty();
                database.TableReclamation.RunBatch(guard.Token);
            }
        });
        var checkpoint = Actor(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                if (!database.Coordinator.TryCheckpoint(TimeSpan.Zero, guard.Token)) { Interlocked.Increment(ref dataDeferrals); }
                if (!catalogGate.TryCheckpoint(TimeSpan.Zero, guard.Token)) { Interlocked.Increment(ref catalogDeferrals); }
                Thread.Yield();
            }
        });
        var all = Task.WhenAll(writers.Concat(new[] { catalog, reclamation, checkpoint }));
        var watch = Stopwatch.StartNew();
        try
        {
            // Act
            while (watch.Elapsed < TimeSpan.FromSeconds(1) || Enumerable.Range(0, writes.Length).Any(i => Interlocked.Read(ref writes[i]) < 32) ||
                Volatile.Read(ref schemas) < 8 || database.TableReclamation.ReclaimedPages < 8 ||
                Volatile.Read(ref dataDeferrals) == 0 || Volatile.Read(ref catalogDeferrals) == 0)
            {
                if (all.IsCompleted) { await all; }
                await Task.Delay(10, guard.Token);
            }
            if (cancelAtCeiling) { guard.Cancel(); guard.Token.ThrowIfCancellationRequested(); }
        }
        catch (OperationCanceledException) when (cancelAtCeiling && guard.IsCancellationRequested) { }
        finally
        {
            Volatile.Write(ref stop, 1);
            // The workload guard may already have fired. Drain actors before engine disposal
            // with an independent hang guard, including their cancellation/fault completions.
            try { await all.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (OperationCanceledException) when (guard.IsCancellationRequested)
            {
                all.IsCompleted.ShouldBeTrue();
                if (all.Exception is { } failures)
                { failures.Flatten().InnerExceptions.ShouldAllBe(error => error is OperationCanceledException); }
                if (!cancelAtCeiling) { throw; }
            }
        }
        // Assert
        writes.ShouldAllBe(count => count >= 32);
        schemas.ShouldBeGreaterThanOrEqualTo(8);
        database.TableReclamation.ReclaimedPages.ShouldBeGreaterThanOrEqualTo(8);
        dataDeferrals.ShouldBeGreaterThan(0); catalogDeferrals.ShouldBeGreaterThan(0);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        recorder.Events.ShouldContain(e => e.EventId == 21 && Equals(e.Payload![1], "SqlCatalog"));
        database.Coordinator.TryCheckpoint(TimeSpan.Zero, CancellationToken.None).ShouldBeTrue();
        catalogGate.TryCheckpoint(TimeSpan.Zero, CancellationToken.None).ShouldBeTrue();
        _output.WriteLine($"free-running writes={string.Join(',', writes)}; schemas={schemas}; retired={retired}; reclaimed={database.TableReclamation.ReclaimedPages}; deferrals={dataDeferrals}/{catalogDeferrals}");
    }
}
