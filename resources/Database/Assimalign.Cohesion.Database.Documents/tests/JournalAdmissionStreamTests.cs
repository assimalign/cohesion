using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection(nameof(DocumentWorkerResilienceTests))]
public sealed class JournalAdmissionStreamTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Upload lifetime cancellation wakes final chunk admission during flush and disposal (#1283)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CompleteAsync_PausedChunkAdmission_ShouldHonorUploadLifetimeCancellation(int completion)
    {
        // Arrange: a short buffered upload has not yet opened its final physical bracket.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var lifetime = new CancellationTokenSource();
        await using var engine = DocumentDatabaseEngine.Create("stream-pause", new()
        { CheckpointJournalSize = 0, CheckpointInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("paused", guard.Token);
        var gate = TransactionCheckpointInterlock.ForStorage(database.DataStorage);
        var context = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, guard.Token);
        await using var stream = database.DataStorage.OpenWrite(database.Coordinator, context,
            _ => ValueTask.CompletedTask, () => ValueTask.CompletedTask, lifetime.Token);
        await stream.WriteAsync("{}"u8.ToArray(), guard.Token);
        using var outside = database.DataStorage.BeginTransaction("OutsideTest");
        gate.JournalSizeLimit = 2; database.DataStorage.CheckpointJournalSize = 1;
        gate.IsAdmissionPaused.ShouldBeTrue();
        var source = EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        var finishing = completion == 0 ? stream.DisposeAsync().AsTask()
            : stream.FlushAsync(completion == 1 ? CancellationToken.None : guard.Token);
        try
        {
            while (!recorder.Events.Any(e => e.EventId == 25)) { await Task.Delay(1, guard.Token); }
            // Act: disposal supplies no per-call token; a flush may supply a different one.
            lifetime.Cancel();
            OperationCanceledException? canceled = null;
            try { await finishing.WaitAsync(guard.Token); }
            catch (OperationCanceledException exception) { canceled = exception; }
            // Assert: only the lifetime cancellation, never the hang guard, ended admission.
            canceled.ShouldNotBeNull(); guard.IsCancellationRequested.ShouldBeFalse();
            gate.IsAdmissionPaused.ShouldBeTrue(); database.IsOffline.ShouldBeFalse();
            recorder.Events.ShouldNotContain(e => e.EventId == 0);
        }
        finally
        {
            lifetime.Cancel(); outside.Rollback(); gate.JournalSizeLimit = 0;
            try { await finishing.WaitAsync(TimeSpan.FromSeconds(30)); } catch (OperationCanceledException) { }
            await database.Coordinator.RollbackAsync(context);
        }
    }
}
