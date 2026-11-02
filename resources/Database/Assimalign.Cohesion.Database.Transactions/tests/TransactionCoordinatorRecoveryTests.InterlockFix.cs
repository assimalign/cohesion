using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions.Internal;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

public partial class TransactionCoordinatorRecoveryTests
{
    private readonly ITestOutputHelper _output;
    public TransactionCoordinatorRecoveryTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Successful checkpoints consume requests received while running (#1283)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Checkpoint_RequestDuringCheckpoint_ShouldRunExactlyOnce(int kind)
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int checkpoints = 0;
        coordinator.BeforeCheckpoint = _ =>
        {
            Interlocked.Increment(ref checkpoints);
            entered.Set();
            release.Wait(guard.Token);
        };
        var running = Task.Factory.StartNew(() =>
        {
            if (kind == 0)
            {
                gate.Wait(guard.Token, "Statement");
                // A separate thread requests the first deferral without inheriting apply state.
                Task.Factory.StartNew(() => coordinator.TryCheckpoint(TimeSpan.Zero, guard.Token),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult().ShouldBeFalse();
                gate.Release();
            }
            else if (kind == 1) { coordinator.TryCheckpoint(TimeSpan.Zero, guard.Token).ShouldBeTrue(); }
            else { coordinator.Checkpoint(guard.Token); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            entered.Wait(guard.Token);
            // Act: this request is covered by the in-progress sharp checkpoint.
            coordinator.TryCheckpoint(TimeSpan.Zero, guard.Token).ShouldBeFalse();
            release.Set();
            await running.WaitAsync(guard.Token);
            gate.Wait(guard.Token); gate.Release();
            // Assert: a later holder cannot inherit a stale request.
            checkpoints.ShouldBe(1);
        }
        finally { release.Set(); await running.WaitAsync(guard.Token); }
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Size-due release checkpoints without a worker or interval request (#1283)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Release_SizeDueWithoutWorker_ShouldCheckpointBeforeNextAdmission(bool bound)
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = bound ? new TransactionCoordinator(storage, storage.Log, storage.Records) : null;
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        storage.CheckpointJournalSize = 1;
        storage.OnCheckpointNeeded = () => { };
        // Act
        using (gate.Enter(bound ? "Statement" : "Catalog"))
        { using var bracket = storage.BeginTransaction(); bracket.Commit(); }
        // Assert: the gate's release, without a worker request, truncated this cycle.
        storage.IsCheckpointDue(TimeSpan.MaxValue).ShouldBeFalse();
        long lsn = storage.Log.LastLsn;
        using (gate.Enter("Idle")) { }
        storage.Log.LastLsn.ShouldBe(lsn);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Busy checkpoints wait for another worker request before foreground retry (#1283)")]
    public async Task Release_OutsideBracketBusy_ShouldAvoidRetryStorm()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        using var outside = storage.BeginTransaction();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int attempts = 0;
        coordinator.BeforeCheckpoint = _ => attempts++;
        storage.CheckpointJournalSize = 1;
        // Act
        gate.Wait();
        coordinator.TryCheckpoint(TimeSpan.Zero, guard.Token).ShouldBeFalse();
        gate.Release();
        for (int i = 0; i < 10; i++) { using (gate.Enter("Statement")) { } }
        attempts.ShouldBe(1);
        gate.Wait();
        coordinator.TryCheckpoint(TimeSpan.Zero, guard.Token).ShouldBeFalse();
        gate.Release();
        // Assert
        attempts.ShouldBe(2);
        outside.Rollback();
        coordinator.Checkpoint(guard.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Deferred I/O failure suppresses foreground retries until worker collection (#1283)")]
    public async Task Release_UncollectedFailure_ShouldAvoidFailureLoop()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        var failure = new System.IO.IOException("injected");
        int attempts = 0;
        coordinator.BeforeCheckpoint = _ => { attempts++; throw failure; };
        storage.CheckpointJournalSize = 1;
        // Act
        gate.Wait();
        using (var bracket = storage.BeginTransaction()) { bracket.Commit(awaitDurability: false); }
        gate.Release();
        for (int i = 0; i < 10; i++) { using (gate.Enter("Statement")) { } }
        // Assert
        attempts.ShouldBe(1);
        Should.Throw<System.IO.IOException>(() => coordinator.TryCheckpoint(TimeSpan.Zero, CancellationToken.None)).ShouldBeSameAs(failure);
        coordinator.BeforeCheckpoint = null;
        coordinator.Checkpoint();
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - A storage binds one live coordinator and unbinds on disposal (#1283)")]
    public async Task ConfigureCheckpoint_SecondLiveCoordinator_ShouldRefuseAndPermitRebindAfterDisposal()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        var first = new TransactionCoordinator(storage, storage.Log, storage.Records);
        try
        {
            // Act / Assert
            Should.Throw<InvalidOperationException>(() => new TransactionCoordinator(storage, storage.Log, storage.Records));
        }
        finally { await first.DisposeAsync(); }
        await using var second = new TransactionCoordinator(storage, storage.Log, storage.Records);
        await first.DisposeAsync(); // A repeated old disposal must not detach the new coordinator.
        int calls = 0;
        second.BeforeCheckpoint = _ => calls++;
        second.Checkpoint();
        calls.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Cancellation of queued async admission cannot consume a permit (#1283)")]
    public async Task WaitAsync_CanceledWhileQueued_ShouldLeaveNextAdmissionUsable()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var canceled = new CancellationTokenSource();
        gate.Wait(guard.Token);
        // Act
        var waiting = gate.WaitAsync(canceled.Token).AsTask();
        waiting.IsCompleted.ShouldBeFalse();
        canceled.Cancel();
        try { await Should.ThrowAsync<OperationCanceledException>(() => waiting); }
        finally { gate.Release(); }
        // Assert
        await gate.WaitAsync(guard.Token);
        gate.Release();
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Bound bracket and lease allocations use the minimum of three warmed rounds (#1283)")]
    public async Task Enter_BoundCoordinatorHotPaths_ShouldAllocateNoLeaseBytes()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        long leaseBytes = long.MaxValue, bracketBytes = long.MaxValue;
        long leaseTicks = long.MaxValue, bracketTicks = long.MaxValue;
        const int count = 4096;
        // Act
        for (int round = 0; round < 4; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread(), started = Stopwatch.GetTimestamp();
            for (int i = 0; i < count; i++) { using (gate.Enter("Catalog", 42)) { } }
            long leases = GC.GetAllocatedBytesForCurrentThread() - before, leaseElapsed = Stopwatch.GetTimestamp() - started;
            before = GC.GetAllocatedBytesForCurrentThread(); started = Stopwatch.GetTimestamp();
            for (int i = 0; i < count; i++)
            { using var bracket = storage.BeginTransaction("Statement", 42); bracket.Commit(awaitDurability: false); }
            long brackets = GC.GetAllocatedBytesForCurrentThread() - before, bracketElapsed = Stopwatch.GetTimestamp() - started;
            if (round == 0) { continue; }
            leaseBytes = Math.Min(leaseBytes, leases); bracketBytes = Math.Min(bracketBytes, brackets);
            leaseTicks = Math.Min(leaseTicks, leaseElapsed); bracketTicks = Math.Min(bracketTicks, bracketElapsed);
        }
        // Assert: throughput is diagnostic; the lease must have no per-call allocation.
        _output.WriteLine($"bound hot path: Enter={leaseBytes / (double)count:F2} B/call, {leaseTicks * 1e9 / Stopwatch.Frequency / count:F1} ns/call; Begin+Commit={bracketBytes / (double)count:F2} B/call, {bracketTicks * 1e9 / Stopwatch.Frequency / count:F1} ns/call");
        leaseBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Undo and prune releases service a checkpoint before returning (#1283)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Release_UndoOrPrune_ShouldServiceDeferredCheckpoint(bool prune)
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        var records = new InterceptRecordSpace(storage.Records);
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, records);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var writer = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, guard.Token);
        var location = await coordinator.ApplyStatementAsync(writer, bracket =>
        {
            var row = storage.Insert(bracket, Stamped(writer.Sequence, TransactionSequence.None));
            coordinator.VersionStore.RecordCreated(writer.Sequence, row.PageId, row.SlotIndex);
            return row;
        }, guard.Token);
        if (prune)
        {
            await coordinator.CommitAsync(writer, guard.Token);
            var deleter = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, guard.Token);
            await coordinator.ApplyStatementAsync(deleter, bracket =>
            {
                storage.Update(bracket, location.PageId, location.SlotIndex, Stamped(writer.Sequence, deleter.Sequence));
                coordinator.VersionStore.RecordTombstoned(deleter.Sequence, location.PageId, location.SlotIndex);
                return 0;
            }, guard.Token);
            await coordinator.CommitAsync(deleter, guard.Token);
        }
        int requests = 0, checkpoints = 0;
        coordinator.BeforeCheckpoint = _ => checkpoints++;
        using var recorder = new EventSourceRecorder(TransactionEventSource.Log, EventLevel.Verbose);
        records.Deleting = () =>
        {
            Task.Factory.StartNew(() => coordinator.TryCheckpoint(TimeSpan.Zero, guard.Token),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult().ShouldBeFalse();
            requests++;
        };
        // Act
        if (prune) { coordinator.RunVersionPurgePass(guard.Token).ShouldBe(1); }
        else { await coordinator.RollbackAsync(writer, guard.Token); }
        // Assert
        requests.ShouldBe(1); checkpoints.ShouldBe(1);
        recorder.Events.ShouldContain(e => e.EventId == 21 && Equals(e.Payload![1], prune ? "Prune" : "Undo"));
        RecordCount(storage).ShouldBe(0);
    }

    private sealed class InterceptRecordSpace : TransactionRecordSpace
    {
        private readonly TransactionRecordSpace _inner;
        internal Action? Deleting;
        internal InterceptRecordSpace(TransactionRecordSpace inner) { _inner = inner; }
        protected override ReadOnlyMemory<byte> ReadCore(PageId pageId, int slotIndex) => _inner.Read(pageId, slotIndex);
        protected override void UpdateCore(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
            => _inner.Update(transaction, pageId, slotIndex, record);
        protected override void DeleteCore(StorageTransaction transaction, PageId pageId, int slotIndex)
        { Deleting?.Invoke(); _inner.Delete(transaction, pageId, slotIndex); }
        protected override ulong PackLocationCore(PageId pageId, int slotIndex) => _inner.PackLocation(pageId, slotIndex);
        protected override (PageId PageId, int SlotIndex) UnpackLocationCore(ulong location) => _inner.UnpackLocation(location);
    }

#if !DEBUG
    [Fact(DisplayName = "Cohesion Test [Transactions] - Cross-thread lease disposal refuses and reports the entering thread (#1283)")]
    public async Task Dispose_CrossThreadLease_ShouldRefuseWithoutReleasingAdmission()
    {
        // Arrange: Debug.Assert additionally fails fast in Debug; run the refusal in Release.
        using var storage = CoordinatorStorage.Create();
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var recorder = new EventSourceRecorder(TransactionEventSource.Log, EventLevel.Verbose);
        IDisposable? lease = null;
        int enteringThread = 0;
        var holder = Task.Factory.StartNew(() =>
        {
            enteringThread = Environment.CurrentManagedThreadId;
            lease = gate.Enter("Catalog");
            entered.Set(); release.Wait(guard.Token);
            lease.Dispose();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            entered.Wait(guard.Token);
            // Act / Assert: the entering actor must still own admission after refusal.
            Should.Throw<StorageTransactionException>(() => lease!.Dispose());
            gate.TryCheckpoint(TimeSpan.Zero, guard.Token).ShouldBeFalse();
            var mismatch = recorder.Events.Single(e => e.EventId == 22);
            mismatch.PayloadNames.ShouldBe(new[] { "database", "enteringThread", "disposingThread" });
            mismatch.Payload![1].ShouldBe((long)enteringThread);
            mismatch.Payload![2].ShouldBe((long)Environment.CurrentManagedThreadId);
            recorder.Events.ShouldNotContain(e => e.EventId == 0);
        }
        finally { release.Set(); await holder.WaitAsync(guard.Token); }
    }
#endif

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_state")]
    private static extern ref object InterlockState(TransactionCheckpointInterlock gate);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_transactionLock")]
    private static extern ref object TransactionMonitor(Storage.Storage storage);

    [Theory(DisplayName = "Cohesion Test [Transactions] - Blocker event listeners execute outside hot monitors (#1283)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checkpoint_BlockerDiagnostics_ShouldReleaseHotMonitorBeforeEvent(bool physical)
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        object monitor = physical ? TransactionMonitor(storage) : InterlockState(gate);
        using var listener = new MonitorListener(physical ? "Assimalign.Cohesion.Database.Storage" : "Assimalign.Cohesion.Database.Transactions",
            physical ? 19 : 21, monitor);
        // Act
        if (physical)
        {
            using var bracket = storage.BeginTransaction("Outside");
            Should.Throw<StorageTransactionException>(() => storage.Checkpoint());
        }
        else
        {
            gate.Wait();
            try { coordinator.TryCheckpoint(TimeSpan.Zero, CancellationToken.None).ShouldBeFalse(); }
            finally { gate.Release(); }
        }
        // Assert
        listener.Events.ShouldBe(1);
        listener.MonitorWasFree.ShouldBeTrue();
    }

    private sealed class MonitorListener : EventListener
    {
        private readonly string _source;
        private readonly int _event;
        private readonly object _monitor;
        internal int Events;
        internal bool MonitorWasFree;
        internal MonitorListener(string source, int eventId, object monitor)
        {
            _source = source; _event = eventId; _monitor = monitor;
            foreach (var existing in EventSource.GetSources().Where(s => s.Name == source)) { EnableEvents(existing, EventLevel.Verbose, EventKeywords.All); }
        }
        protected override void OnEventSourceCreated(EventSource source)
        { if (source.Name == _source) { EnableEvents(source, EventLevel.Verbose, EventKeywords.All); } }
        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (data.EventSource.Name != _source || data.EventId != _event) { return; }
            Events++;
            MonitorWasFree = Task.Factory.StartNew(() =>
            {
                bool taken = Monitor.TryEnter(_monitor);
                if (taken) { Monitor.Exit(_monitor); }
                return taken;
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();
        }
    }
}
