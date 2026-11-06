using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions.Internal;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

public partial class TransactionCoordinatorRecoveryTests
{
    [Fact(DisplayName = "Cohesion Test [Transactions] - Admission observation, back-out and checkpoint race through at least 1000 episodes (#1283)")]
    public async Task WaitAsync_ObserveBackoutAndCheckpointRace_ShouldResumeAtLeastOneThousandEpisodes()
    {
        // Arrange: this partial belongs to the non-parallel Journal enrollment collection.
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var stop = new CancellationTokenSource();
        using var recorder = new EventSourceRecorder(TransactionEventSource.Log, EventLevel.Verbose);
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        storage.CheckpointJournalSize = 1;
        gate.JournalSizeLimit = 2;
        long signals = 0;
        storage.OnCheckpointNeeded = () => Interlocked.Increment(ref signals);
        var observer = Task.Factory.StartNew(() =>
        {
            while (!stop.IsCancellationRequested)
            { gate.ObserveAdmissionPause(); Thread.Yield(); ceiling.Token.ThrowIfCancellationRequested(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var exposure = Stopwatch.StartNew();
        int cycles = 0;
        try
        {
            // Act: force the otherwise narrow back-out race, while ObservePause races
            // checkpoint completion on an independent thread throughout every episode.
            do
            {
                gate.Wait(ceiling.Token, drain: true);
                long waits = TransactionEventSource.Log.AdmissionWaits;
                var waiting = gate.WaitAsync(ceiling.Token, "RacingWriter").AsTask();
                using (var bracket = storage.BeginTransaction()) { bracket.Commit(); }
                gate.ObserveAdmissionPause().ShouldBeTrue();
                (await Task.Run(() => gate.TryCheckpoint(TimeSpan.Zero, ceiling.Token), ceiling.Token)).ShouldBeFalse();
                long signaled = Interlocked.Read(ref signals);
                gate.Release(allowCheckpoint: false);
                while (TransactionEventSource.Log.AdmissionWaits == waits) { await Task.Yield(); ceiling.Token.ThrowIfCancellationRequested(); }
                Interlocked.Read(ref signals).ShouldBeGreaterThan(signaled);
                await Task.Run(() => coordinator.Checkpoint(ceiling.Token), ceiling.Token);
                await waiting.WaitAsync(ceiling.Token);
                gate.Release();
                cycles++;
            }
            while (cycles < 1000 || exposure.Elapsed < TimeSpan.FromSeconds(1));
        }
        finally { stop.Cancel(); await observer.WaitAsync(ceiling.Token); }
        // Assert: counters decide completion; the ceiling only detects a hang.
        var pauses = recorder.Events.Where(e => e.EventId == 23).ToArray();
        var resumes = recorder.Events.Where(e => e.EventId == 24).ToArray();
        pauses.Length.ShouldBeGreaterThanOrEqualTo(1000);
        resumes.Length.ShouldBe(pauses.Length);
        resumes.Select(e => (long)e.Payload![3]!).Order().ShouldBe(pauses.Select(e => (long)e.Payload![6]!).Order());
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        _output.WriteLine($"Admission race: cycles={cycles}, pauses={pauses.Length}, resumes={resumes.Length}, backoutSignals={signals}");
    }
}
