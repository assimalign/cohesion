using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

[Collection(nameof(KeyValueWorkerResilienceTests))]
public sealed class JournalAdmissionPauseTests
{
    [Fact(DisplayName = "Cohesion Test [KeyValuePair] - Checkpoint never waits for the catalog monitor held by a paused writer (#1283)")]
    public async Task CheckpointWorker_PausedCatalogWriter_ShouldCheckpointBeforeReadingMetadata()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = KeyValueDatabaseEngine.Create("catalog-pause", new()
        { CheckpointJournalSize = 0, CheckpointInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("paused", guard.Token);
        var registrations = database.IndexManager.ExportRegistrations();
        var gate = TransactionCheckpointInterlock.ForStorage(database.CatalogStorage);
        using (var bracket = database.CatalogStorage.BeginTransaction()) { bracket.Commit(); }
        gate.JournalSizeLimit = 2; database.CatalogStorage.CheckpointJournalSize = 1;
        gate.IsAdmissionPaused.ShouldBeTrue();
        var source = EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        int attempts = 0;
        var worker = engine.Workers.OfType<KeyValueCheckpointWorker>().Single();
        worker.BeforeCheckpoint = _ => Interlocked.Increment(ref attempts);
        var writing = Task.Factory.StartNew(() => database.Catalog.SaveIndexRegistrationsAsync(registrations, guard.Token).GetAwaiter().GetResult(),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            while (!recorder.Events.Any(e => e.EventId == 25)) { await Task.Delay(1, guard.Token); }
            // Act: this is the real worker path, including its former registration backstop.
            engine.CheckpointNeededSignal.Set();
            await writing.WaitAsync(guard.Token);
            // Assert: the catalog gate resumed without ever acquiring catalog _sync in checkpoint.
            Volatile.Read(ref attempts).ShouldBeGreaterThan(0);
            gate.IsAdmissionPaused.ShouldBeFalse(); database.IsOffline.ShouldBeFalse();
            recorder.Events.ShouldNotContain(e => e.EventId == 0);
        }
        finally
        {
            worker.BeforeCheckpoint = null; gate.CloseAdmission();
            try { await writing.WaitAsync(guard.Token); }
            catch (Exception exception) when (exception is ObjectDisposedException or OperationCanceledException) { }
        }
    }
}
