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
    [Theory(DisplayName = "Cohesion Test [KeyValuePair] - Catalog mutations refuse a pause and release their metadata monitor (#1283)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointWorker_PausedCatalogMutation_ShouldRefuseThenRecover(bool format)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        await using var engine = KeyValueDatabaseEngine.Create("catalog-pause", new()
        { CheckpointJournalSize = 0, CheckpointInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("paused", guard.Token);
        var registrations = database.IndexManager.ExportRegistrations();
        var gate = TransactionCheckpointInterlock.ForStorage(database.CatalogStorage);
        using (var bracket = database.CatalogStorage.BeginTransaction()) { bracket.Commit(); }
        var source = EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        int attempts = 0;
        var worker = engine.Workers.OfType<KeyValueCheckpointWorker>().Single();
        worker.BeforeCheckpoint = _ => { Interlocked.Increment(ref attempts); release.Wait(guard.Token); };
        gate.JournalSizeLimit = 2; database.CatalogStorage.CheckpointJournalSize = 1;
        gate.ObserveAdmissionPause().ShouldBeTrue();
        try
        {
            // Act: neither public catalog mutator may park while retaining _sync.
            Should.Throw<TransactionAdmissionCanceledException>(() =>
            {
                if (format) { database.Catalog.SetEntrySpaceFormatVersionAsync(database.Catalog.EntrySpaceFormatVersion, guard.Token).GetAwaiter().GetResult(); }
                else { database.Catalog.SaveIndexRegistrationsAsync(registrations, guard.Token).GetAwaiter().GetResult(); }
            });
            guard.IsCancellationRequested.ShouldBeFalse();
            database.Catalog.GetIndexRegistrations().Count.ShouldBe(registrations.Count);
            recorder.Events.ShouldContain(e => e.EventId == 25);
            release.Set();
            engine.CheckpointNeededSignal.Set();
            while (gate.IsAdmissionPaused) { await Task.Delay(1, guard.Token); }
            // Assert: the catalog gate resumed without ever acquiring catalog _sync in checkpoint.
            Volatile.Read(ref attempts).ShouldBeGreaterThan(0);
            gate.IsAdmissionPaused.ShouldBeFalse(); database.IsOffline.ShouldBeFalse();
            recorder.Events.ShouldNotContain(e => e.EventId == 0);
        }
        finally
        {
            worker.BeforeCheckpoint = null; release.Set(); gate.JournalSizeLimit = 0;
        }
    }
}
