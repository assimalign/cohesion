using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>
/// Attribution probe and deterministic controls for #1283, not a journal-cap regression test.
/// Set COHESION_DB_PROBES=1 to run the load probe, with detailed console logging.
/// Peak includes checkpoint-start events and lengths after each commit/trigger; it is a lower
/// bound on the instantaneous peak, not an estimate of bytes appended or of checkpoint counts.
/// </summary>
[Collection(nameof(BlobDatabaseEventSourceCollection))]
public sealed class BlobCheckpointGrowthTests
{
    private const long journalSize = 64 * 1024;
    private readonly ITestOutputHelper _output;

    public BlobCheckpointGrowthTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DatabaseProbeTheory(DisplayName = "Cohesion Test [Database.Blob] - Checkpoint growth: attribute four short-transaction writers (#1283)")]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Checkpoint_FourContinuousWriters_ShouldReportGrowthAndExerciseDeferral(int competitors)
    {
        // Arrange: restore process affinity even if setup, a writer, or an assertion fails.
        using var affinity = new AffinityScope();
        for (int round = 0; round < 3; round++)
        {
            using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var stop = new CancellationTokenSource();
            await using var engine = BlobDatabaseEngine.Create("checkpoint-probe", new()
            {
                CheckpointJournalSize = journalSize,
                CheckpointInterval = TimeSpan.FromHours(1),
                PageWriteBackInterval = TimeSpan.FromHours(1),
                MaintenanceInterval = TimeSpan.FromHours(1),
            });
            var database = await engine.CreateDatabaseAsync("growth", ceiling.Token);
            await AutocommitContainer.CreateAsync(database, "files", ceiling.Token);
            using var probe = new CheckpointProbe(database.DataStorage.Name, engine.Name, countStorageTransactionExceptions: true);
            var signal = database.DataStorage.OnCheckpointNeeded;
            database.DataStorage.OnCheckpointNeeded = () =>
            {
                probe.Triggered(database.DataStorage.JournalLength);
                signal?.Invoke();
            };
            var counts = new long[4];
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var burners = Enumerable.Range(0, competitors).Select(_ => Task.Factory.StartNew(() =>
            {
                while (!stop.IsCancellationRequested && !ceiling.IsCancellationRequested)
                {
                    Thread.SpinWait(4096);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            var writers = Enumerable.Range(0, 4).Select(writer => Task.Run(async () =>
            {
                await ready.Task;
                await using var session = await database.CreateSessionAsync(ceiling.Token);
                var files = await session.GetContainerAsync("files", ceiling.Token);
                byte[] payload = new byte[1024]; // Below a chunk: this is not #1270's single large statement.
                new Random(1283 + writer).NextBytes(payload);
                payload[0] = (byte)writer;
                long next = 0;
                while (!stop.IsCancellationRequested && !ceiling.IsCancellationRequested)
                {
                    // Each operation is an autocommit; stream disposal publishes the blob.
                    // Fresh names keep the statement work short instead of growing a hot blob's
                    // version chain as an overwrite-only workload runs longer.
                    await using (var upload = await files.OpenWriteAsync($"writer-{writer}-{next++}", cancellationToken: ceiling.Token))
                    {
                        await upload.WriteAsync(payload, ceiling.Token);
                    }
                    Interlocked.Increment(ref counts[writer]);
                    probe.Written(database.DataStorage.JournalLength);
                }
            }, CancellationToken.None)).ToArray();

            CheckpointProbe.Snapshot measured;
            try
            {
                // Act: minimum duration, then counters prove every writer and the gate race ran.
                ready.SetResult();
                do
                {
                    await Task.Delay(10, ceiling.Token);
                    foreach (var writer in writers.Where(writer => writer.IsCompleted))
                    {
                        await writer; // Surface a writer failure instead of timing out on its counters.
                    }
                    measured = probe.Read();
                }
                while (measured.Elapsed < TimeSpan.FromSeconds(2) || measured.Checkpoints < 4 ||
                    measured.Deferred == 0 || Enumerable.Range(0, counts.Length).Any(writer => Volatile.Read(ref counts[writer]) < 32));
            }
            finally
            {
                stop.Cancel();
                try
                {
                    await Task.WhenAll(writers.Concat(burners));
                }
                finally
                {
                    database.DataStorage.OnCheckpointNeeded = signal;
                }
            }

            // Assert: report overshoot and busy-storage counts; the growth cause remains open.
            measured = probe.Read();
            string report = $"round={round + 1}, competitors={competitors}, affinity={affinity.Description}; " + measured.Describe(journalSize);
            _output.WriteLine(report);
            counts.ShouldAllBe(count => count >= 32, report);
            measured.Triggers.ShouldBeGreaterThan(0, report);
            measured.Checkpoints.ShouldBeGreaterThanOrEqualTo(4, report);
            measured.Deferred.ShouldBeGreaterThan(0, report);
            measured.Offline.ShouldBe(0, report);
            measured.Failures.ShouldBe(0, report);
            measured.EventErrors.ShouldBe(0, report);
            measured.WorkerPasses.ShouldBeGreaterThan(0, report);
            engine.State.ShouldBe(EngineState.Running, report);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Checkpoint growth: an outside bracket positively identifies skipped deferrals across statement exits (#1283)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TryCheckpoint_OutsideBracket_ShouldIdentifyBusyStorageAndRetryAfterRelease(int exit)
    {
        // Arrange: an otherwise untouched raw bracket is enough to prevent a sharp checkpoint.
        // Request checkpoints manually so the autonomous worker cannot consume the pending
        // request between releasing the outside bracket and the next statement.
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = BlobDatabaseEngine.Create("checkpoint-control", new()
        {
            CheckpointJournalSize = 0,
            CheckpointInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("control", ceiling.Token);
        await using var session = await database.CreateSessionAsync(ceiling.Token);
        var files = await session.CreateContainerAsync("files", ceiling.Token);
        using var probe = new CheckpointProbe(database.DataStorage.Name, engine.Name);
        using var outside = database.DataStorage.BeginTransaction();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var statementCancellation = new CancellationTokenSource();
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, ceiling.Token);
        byte[] payload = new byte[1024];
        new Random(1283).NextBytes(payload);
        Task? applying = null;
        try
        {
            for (int i = 0; database.DataStorage.JournalLength <= 4 * journalSize && i < 1024; i++)
            {
                await using (var upload = await files.OpenWriteAsync("one", cancellationToken: ceiling.Token))
                {
                    await upload.WriteAsync(payload, ceiling.Token);
                }
                probe.Written(database.DataStorage.JournalLength);
            }
            database.DataStorage.JournalLength.ShouldBeGreaterThan(4 * journalSize);
            long checkpoints = probe.Read().Checkpoints;
            applying = Task.Factory.StartNew(() => database.Coordinator.ApplyStatementAsync(context, _ =>
            {
                entered.Set();
                release.Wait(ceiling.Token);
                if (exit == 2)
                {
                    statementCancellation.Cancel();
                    statementCancellation.Token.ThrowIfCancellationRequested();
                }
                return exit switch
                {
                    1 => throw new InvalidOperationException("Probe statement failed."),
                    _ => 0,
                };
            }, cancellationToken: statementCancellation.Token).AsTask().GetAwaiter().GetResult(), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            entered.Wait(ceiling.Token);

            // Act: force the request to the gate holder; all three finally paths must try it.
            database.Coordinator.TryCheckpoint(TimeSpan.Zero, ceiling.Token).ShouldBeFalse();
            release.Set();
            if (exit == 0)
            {
                await applying;
            }
            else if (exit == 1)
            {
                await Should.ThrowAsync<InvalidOperationException>(async () => await applying);
            }
            else
            {
                await Should.ThrowAsync<OperationCanceledException>(async () => await applying);
            }
            var busy = probe.Read();
            busy.Checkpoints.ShouldBe(checkpoints);
            busy.BracketOpen.ShouldBeGreaterThan(0);
            outside.Rollback();
            await database.Coordinator.ApplyStatementAsync(context, _ => 0, cancellationToken: ceiling.Token);
            var resumed = probe.Read();

            // Assert: the pending request runs at a statement end once the outside bracket ends.
            resumed.Checkpoints.ShouldBeGreaterThan(checkpoints);
            resumed.Failures.ShouldBe(0);
            resumed.Offline.ShouldBe(0);
            resumed.EventErrors.ShouldBe(0);
            database.DataStorage.JournalLength.ShouldBeLessThan(journalSize);
            _output.WriteLine($"exit={exit}; {resumed.Describe(journalSize)}");
        }
        finally
        {
            release.Set();
            try
            {
                if (applying is not null)
                {
                    try { await applying; }
                    catch (InvalidOperationException) when (exit == 1) { }
                    catch (OperationCanceledException) when (exit == 2 || ceiling.IsCancellationRequested) { }
                }
            }
            finally
            {
                if (outside.IsActive)
                {
                    outside.Rollback();
                }
                await database.Coordinator.RollbackAsync(context, CancellationToken.None);
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Checkpoint growth: collecting a deferred lane rechecks due work in the same pass (#1283)")]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RunIteration_DeferredLaneAndNewSizeWake_ShouldRecheckDueWorkInTheCollectionPass(bool retryDue, bool retryFinished)
    {
        // Arrange: use the model's existing internal shared worker through its own test grant.
        // A collected deferral must not consume the size wake without rechecking due work.
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var signal = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int attempts = 0;
        await using var worker = new LaneProbeWorker(signal, () =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                entered.Set();
                release.Wait(ceiling.Token);
                return false; // A checkpoint taken over by a statement has this lane outcome.
            }
            return retryFinished;
        });
        var firstPass = Task.Factory.StartNew(() => worker.RunIteration(ceiling.Token),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            entered.Wait(ceiling.Token);

            // Act: a new size wake detaches the lane; completion wakes the worker again. Waiting
            // for that second signal proves the false outcome is published before its collection.
            signal.Set();
            await firstPass.WaitAsync(ceiling.Token);
            signal.Reset();
            release.Set();
            signal.Wait(ceiling.Token);
            worker.Due = retryDue;
            using var probe = new CheckpointProbe("lane-control", "checkpoint-lane-probe");
            worker.RunIteration(ceiling.Token);

            // Assert: collection checks due work once and attempts at most one new checkpoint,
            // even when that attempt defers again. Unfinished work is reported once per pass.
            signal.IsSet.ShouldBeFalse();
            Volatile.Read(ref attempts).ShouldBe(retryDue ? 2 : 1);
            worker.DueChecks.ShouldBe(2);
            probe.Read().WorkerUnfinished.ShouldBe(1);
            _output.WriteLine($"new size wake + deferred lane: collection pass made 1 due check / {(retryDue ? 1 : 0)} checkpoint attempts; retry finished={retryFinished}");
        }
        finally
        {
            release.Set();
            await firstPass;
        }
    }

    private sealed class LaneProbeWorker : DatabaseCheckpointWorker<object>, IAsyncDisposable
    {
        private readonly object _database = new();
        private readonly ManualResetEventSlim _signal;
        private readonly Func<bool> _checkpoint;
        private int _dueChecks;

        internal LaneProbeWorker(ManualResetEventSlim signal, Func<bool> checkpoint)
            : base("checkpoint-lane-probe", TimeSpan.FromHours(1), long.MaxValue)
        {
            _signal = signal;
            _checkpoint = checkpoint;
        }

        internal int DueChecks => Volatile.Read(ref _dueChecks);
        internal bool Due { get; set; } = true;

        // This unowned test worker has no engine pump. Join its lanes before the test closes
        // their signals; no shipped lifecycle member or friend-assembly boundary is widened.
        public ValueTask DisposeAsync() => DisposeAsyncCore();

        protected override ManualResetEventSlim CheckpointNeededSignal => _signal;
        protected override object[] GetDatabases() => [_database];
        protected override string GetName(object database) => "lane-control";
        protected override bool IsOffline(object database) => false;
        protected override bool IsOpen(object database) => true;
        protected override long GetJournalLength(object database) => journalSize;
        protected override bool IsCheckpointDue(object database, TimeSpan interval, DatabaseCheckpointPass.Visit? visit)
        {
            if (visit is not null) { visit.Due = new(false, Due, false, journalSize, journalSize); }
            Interlocked.Increment(ref _dueChecks);
            return Due;
        }
        protected override bool Checkpoint(object database, TimeSpan interval, CancellationToken cancellationToken)
            => _checkpoint();
    }

    private sealed class AffinityScope : IDisposable
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly IntPtr _previous;

        internal AffinityScope()
        {
            if (OperatingSystem.IsWindows())
            {
                _previous = _process.ProcessorAffinity;
                long available = _previous.ToInt64();
                long selected = 0;
                int cores = 0;
                for (int bit = 0; bit < 64 && cores < 3; bit++)
                {
                    long mask = 1L << bit;
                    if ((available & mask) != 0)
                    {
                        selected |= mask;
                        cores++;
                    }
                }
                _process.ProcessorAffinity = (IntPtr)selected;
                Description = $"{cores} CPUs (Windows process)";
            }
            // Linux affinity is per-thread: Process.ProcessorAffinity affects only the main
            // thread, not the writers, burners or checkpoint lanes. Do not claim a workload cap.
        }

        internal string Description { get; } = "not constrained (workload affinity not enforced)";

        public void Dispose()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    _process.ProcessorAffinity = _previous;
                }
            }
            finally
            {
                _process.Dispose();
            }
        }
    }
}
