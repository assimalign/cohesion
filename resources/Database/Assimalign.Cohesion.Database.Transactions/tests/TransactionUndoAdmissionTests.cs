using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions.Internal;
using Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

[Collection("Undo substrate")]
public sealed class TransactionUndoAdmissionTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
    private readonly ITestOutputHelper _output;

    public TransactionUndoAdmissionTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Undo: release between validation and admission leaves the writer usable")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackToMarker_ReleasedBeforeAdmission_ShouldRefuseWithoutPendingUndo(bool outermost)
    {
        // Arrange: release either the only marker or an inner marker with an outer survivor.
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        if (!outermost) { await coordinator.CreateUndoMarkerAsync(writer); }
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        int releases = 0;
        coordinator.UndoMarkerValidated = () =>
        {
            Task release;
            // The baseline rollback owns the gate in this hook. Release is another
            // thread/flow, so it must not inherit that flow's inside-apply sentinel.
            using (ExecutionContext.SuppressFlow())
            {
                release = DedicatedTestTask.Run(() => coordinator.ReleaseUndoMarkerAsync(writer, marker).AsTask());
            }
            release.WaitAsync(_timeout).GetAwaiter().GetResult();
            releases++;
        };

        // Act: the store must revalidate after admission, because release takes no apply gate.
        var failure = await Should.ThrowAsync<TransactionUndoException>(() => coordinator.RollbackToUndoMarkerAsync(writer, marker).AsTask());
        coordinator.UndoMarkerValidated = null;
        bool pending = writer.PartialUndoPending;
        var statementFailure = await Record.ExceptionAsync(() => coordinator.ApplyStatementAsync(writer, _ => 7).AsTask());
        var controlFailure = await Record.ExceptionAsync(() => coordinator.CreateUndoMarkerAsync(writer).AsTask());
        var commitFailure = await Record.ExceptionAsync(() => coordinator.CommitAsync(writer).AsTask());

        // Assert: record all effects before asserting, including commit's baseline CK04009.
        _output.WriteLine($"Releases={releases}; pending={pending}; statement={Describe(statementFailure)}; control={Describe(controlFailure)}; commit={Describe(commitFailure)}");
        releases.ShouldBe(1);
        failure.Code.ShouldBe("CK04008");
        commitFailure.ShouldBeNull();
        statementFailure.ShouldBeNull();
        controlFailure.ShouldBeNull();
        pending.ShouldBeFalse();
        writer.State.ShouldBe(TransactionState.Committed);
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Apply: statements wait for a memory marker control and honor cancellation before admission")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ApplyStatement_MemoryControlInFlight_ShouldWaitAndHonorCancellation(bool release, bool cancel)
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var canceled = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int controls = 0, waits = 0, statements = 0;
        coordinator.UndoControlAdmitted = () =>
        {
            Interlocked.Increment(ref controls);
            entered.Set();
            resume.Wait(_timeout).ShouldBeTrue();
        };
        coordinator.Manager.ApplyWaitingForUndoControl = () =>
        {
            Interlocked.Increment(ref waits);
            waiting.TrySetResult();
        };
        var control = DedicatedTestTask.Run(async () =>
        {
            if (release) { await coordinator.ReleaseUndoMarkerAsync(writer, marker); }
            else { await coordinator.CreateUndoMarkerAsync(writer); }
        });
        Task? statement = null;
        bool observedWait = false;
        try
        {
            entered.Wait(_timeout).ShouldBeTrue();

            // Act: either the wait hook or the baseline's immediate exception decides progress.
            statement = DedicatedTestTask.Run(() => coordinator.ApplyStatementAsync(writer,
                _ => Interlocked.Increment(ref statements), canceled.Token).AsTask());
            observedWait = await Task.WhenAny(waiting.Task, statement).WaitAsync(_timeout) == waiting.Task;
            if (cancel) { canceled.Cancel(); }
        }
        finally
        {
            resume.Set();
            await control.WaitAsync(_timeout);
        }
        var failure = await Record.ExceptionAsync(() => statement!.WaitAsync(_timeout));

        // Assert
        _output.WriteLine($"Controls={controls}; waits={waits}; statements={statements}; failure={Describe(failure)}");
        if (cancel) { failure.ShouldBeAssignableTo<OperationCanceledException>()!.CancellationToken.ShouldBe(canceled.Token); }
        else { failure.ShouldBeNull(); }
        observedWait.ShouldBeTrue();
        waits.ShouldBe(1);
        controls.ShouldBe(1);
        statements.ShouldBe(cancel ? 0 : 1);
        if (cancel)
        {
            (await coordinator.ApplyStatementAsync(writer, _ => 7).AsTask().WaitAsync(_timeout)).ShouldBe(7);
        }
        await coordinator.CommitAsync(writer);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Apply: an ending writer with pending partial undo reports transaction aborted")]
    public async Task EnterApply_EndingWithPendingUndo_ShouldReportAborted()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        var index = new UndoTestStorage.Index(storage) { BeforeUndo = () => throw new IOException("Injected partial undo failure") };
        await InsertAsync(coordinator, storage, writer, index);
        await Should.ThrowAsync<IOException>(() => coordinator.RollbackToUndoMarkerAsync(writer, marker).AsTask());
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        int undos = 0;
        index.BeforeUndo = () =>
        {
            Interlocked.Increment(ref undos);
            entered.Set();
            resume.Wait(_timeout).ShouldBeTrue();
        };
        var rollback = DedicatedTestTask.Run(() => coordinator.RollbackAsync(writer).AsTask());
        Exception? failure = null;
        try
        {
            entered.Wait(_timeout).ShouldBeTrue();

            // Act: bypass the physical gate to inspect the admission reason while end drains.
            failure = Record.Exception(() => coordinator.Manager.EnterApply(writer).ExitApply());
        }
        finally
        {
            resume.Set();
            await rollback.WaitAsync(_timeout);
        }

        // Assert
        _output.WriteLine($"Undo calls={undos}; ending admission={Describe(failure)}");
        undos.ShouldBe(1);
        failure.ShouldBeOfType<TransactionAbortedException>();
        writer.PartialUndoPending.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Apply: whole rollback clears partial undo pending even when physical undo is deferred")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnterApply_EndedAfterPendingUndo_ShouldClearPendingAndReportAborted(bool deferred)
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        var index = new UndoTestStorage.Index(storage) { BeforeUndo = () => throw new IOException("Injected undo failure") };
        await InsertAsync(coordinator, storage, writer, index);
        await Should.ThrowAsync<IOException>(() => coordinator.RollbackToUndoMarkerAsync(writer, marker).AsTask());
        if (!deferred) { index.BeforeUndo = null; }

        // Act
        await coordinator.RollbackAsync(writer);
        var failure = Record.Exception(() => coordinator.Manager.EnterApply(writer).ExitApply());
        bool pending = writer.PartialUndoPending;
        bool retained = coordinator.Manager.IsUndoDeferred(writer.Sequence.Value);
        index.BeforeUndo = null;
        await coordinator.Manager.RetryDeferredUndoAsync(CancellationToken.None);

        // Assert
        _output.WriteLine($"Deferred={retained}; pending={pending}; ended admission={Describe(failure)}");
        retained.ShouldBe(deferred);
        failure.ShouldBeOfType<TransactionAbortedException>();
        pending.ShouldBeFalse();
        writer.State.ShouldBe(TransactionState.RolledBack);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo: prefix duplicate lookup allows another writer's statement to complete")]
    public async Task RollbackToMarker_PrefixLookup_ShouldLeaveApplyGateAvailable()
    {
        // Arrange: the second hash is the first prefix entry, after hashing the suffix once.
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var other = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var index = new CountingUndoIndex();
        await coordinator.ApplyStatementAsync(writer, _ =>
        {
            for (int i = 0; i < 100; i++)
            { coordinator.VersionStore.RecordIndexEntryTombstoned(writer.Sequence, index, BitConverter.GetBytes(i), (ulong)i + 1); }
            return 0;
        });
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        await coordinator.ApplyStatementAsync(writer, _ =>
        {
            coordinator.VersionStore.RecordIndexEntryTombstoned(writer.Sequence, index, BitConverter.GetBytes(100), 101);
            return 0;
        });
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        int lookups = 0, statements = 0;
        index.BeforeHash = () =>
        {
            if (index.HashCalls != 2) { return; }
            Interlocked.Increment(ref lookups);
            entered.Set();
            resume.Wait(_timeout).ShouldBeTrue();
        };
        var undo = DedicatedTestTask.Run(() => coordinator.RollbackToUndoMarkerAsync(writer, marker).AsTask());
        Task? statement = null;
        int completedDuringLookup = 0;
        try
        {
            entered.Wait(_timeout).ShouldBeTrue();

            // Act: an available gate completes this entirely synchronous statement immediately.
            statement = coordinator.ApplyStatementAsync(other, _ => Interlocked.Increment(ref statements)).AsTask();
            completedDuringLookup = Volatile.Read(ref statements);
        }
        finally
        {
            resume.Set();
            await undo.WaitAsync(_timeout);
            if (statement is not null) { await statement.WaitAsync(_timeout); }
        }

        // Assert
        _output.WriteLine($"Prefix lookup hooks={lookups}; other statements during lookup={completedDuringLookup}; total={statements}");
        lookups.ShouldBe(1);
        completedDuringLookup.ShouldBe(1);
        statements.ShouldBe(1);
        index.Clears.ShouldBe(1);
        await coordinator.CommitAsync(writer);
        await coordinator.CommitAsync(other);
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Undo: memory controls exclude a second create, release or rollback-to")]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task CreateAndReleaseMarker_SecondControlInFlight_ShouldRefuseAndRemainUsable(bool releaseFirst, int second)
    {
        // Arrange: the hook runs after admission but before any marker mutation.
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        int refused = 0;
        coordinator.UndoControlAdmitted = () =>
        {
            var failure = Should.Throw<TransactionUndoException>(() =>
            {
                if (second == 0) { coordinator.CreateUndoMarkerAsync(writer).GetAwaiter().GetResult(); }
                else if (second == 1) { coordinator.ReleaseUndoMarkerAsync(writer, marker).GetAwaiter().GetResult(); }
                else { coordinator.RollbackToUndoMarkerAsync(writer, marker).GetAwaiter().GetResult(); }
            });
            failure.Code.ShouldBe("CK04009");
            refused++;
        };

        // Act
        if (releaseFirst) { await coordinator.ReleaseUndoMarkerAsync(writer, marker); }
        else { await coordinator.CreateUndoMarkerAsync(writer); }
        coordinator.UndoControlAdmitted = null;

        // Assert
        refused.ShouldBe(1);
        writer.PartialUndoPending.ShouldBeFalse();
        (await coordinator.ApplyStatementAsync(writer, _ => 7)).ShouldBe(7);
        await coordinator.CommitAsync(writer);
    }

    private static string Describe(Exception? exception)
        => exception is TransactionUndoException undo ? $"{exception.GetType().Name} {undo.Code}" : exception?.GetType().Name ?? "none";

    private static ValueTask<ulong> InsertAsync(TransactionCoordinator coordinator, UndoTestStorage storage,
        TransactionContext writer, UndoTestStorage.Index index)
        => coordinator.ApplyStatementAsync(writer, bracket =>
        {
            var record = new byte[RecordVersionStamp.HeaderSize + 1];
            RecordVersionStamp.WriteWriter(record, writer.Sequence);
            var location = storage.Insert(bracket, record);
            ulong packed = UndoTestStorage.Pack(location.PageId, location.SlotIndex);
            coordinator.VersionStore.RecordIndexEntryCreated(writer.Sequence, index, new byte[] { 1 }, packed);
            return packed;
        });
}
