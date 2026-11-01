using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions.Internal;
using Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

[Collection("Undo substrate")]
public sealed class TransactionUndoLockTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Transactions] - Lock journal: releasing or ending the final marked owner restores the null fast path")]
    public async Task ReleaseMarker_LastOwner_ShouldDisableJournalLookup()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var first = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var second = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var firstMarker = await coordinator.CreateUndoMarkerAsync(first);
        var secondMarker = await coordinator.CreateUndoMarkerAsync(second);
        // Act / Assert
        await coordinator.ReleaseUndoMarkerAsync(first, firstMarker);
        coordinator.LockManager.ScopeGrantCounts.ShouldNotBeNull().Writers.ShouldBe(1);
        await coordinator.ReleaseUndoMarkerAsync(second, secondMarker);
        coordinator.LockManager.ScopeGrantCounts.ShouldBeNull();
        await coordinator.CreateUndoMarkerAsync(first);
        await coordinator.CommitAsync(first);
        coordinator.LockManager.ScopeGrantCounts.ShouldBeNull();
        await coordinator.CommitAsync(second);
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Marker boundary: anonymous controls are internal")]
    [InlineData("CreateUndoMarkerAsync")]
    [InlineData("ReleaseUndoMarkerAsync")]
    [InlineData("RollbackToUndoMarkerAsync")]
    [InlineData("GetUndoFailureCode")]
    public void MarkerControl_AnonymousSeam_ShouldRemainInternal(string name)
    {
        // Arrange / Act
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, ".claude", "CLAUDE.md")))
        { directory = directory.Parent ?? throw new DirectoryNotFoundException(); }
        string path = Path.Combine(directory.FullName, "resources", "Database", "Assimalign.Cohesion.Database.Transactions", "src", "TransactionRecordSpace.cs");
        string declaration = File.ReadLines(path).Single(line => line.Contains($" {name}(", StringComparison.Ordinal));
        // Assert: inspect source declarations without reflecting over runtime internals.
        declaration.TrimStart().ShouldStartWith("internal static ");
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Stream boundary: end validates a null owner")]
    public void EndWriteStream_NullContext_ShouldRefuseArgument()
    {
        // Arrange
        var driver = new UndoMarkerDriver();
        // Act / Assert
        Should.Throw<ArgumentNullException>(() => driver.FinishStream(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo streams: an unpaired end cannot underflow or poison later controls")]
    public async Task EndWriteStream_UnpairedEnd_ShouldRefuseWithoutChangingCount()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        // Act / Assert
        Should.Throw<InvalidOperationException>(writer.EndWriteStream);
        writer.HasOpenWriteStreams.ShouldBeFalse();
        writer.BeginWriteStream();
        writer.EndWriteStream();
        Should.Throw<InvalidOperationException>(writer.EndWriteStream);
        writer.HasOpenWriteStreams.ShouldBeFalse();
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        await coordinator.ReleaseUndoMarkerAsync(writer, marker);
        await coordinator.CommitAsync(writer);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Lock journal: same-mode regrants scale with resources and last release disables lookup")]
    public async Task Acquire_SameModeRegrants_ShouldBoundJournalAndResetLastScope()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        // Act
        for (int i = 0; i < 1_000; i++)
        { await coordinator.LockManager.AcquireAsync(writer.Sequence, LockResource.Entry(1, (ulong)(i % 3)), LockMode.Exclusive); }
        // Assert
        coordinator.LockManager.ScopeGrantCounts.ShouldBe((1, 3));
        await coordinator.ReleaseUndoMarkerAsync(writer, marker);
        coordinator.LockManager.ScopeGrantCounts.ShouldBeNull();
        await coordinator.LockManager.AcquireAsync(writer.Sequence, LockResource.Entry(1, 10), LockMode.Exclusive);
        coordinator.LockManager.ScopeGrantCounts.ShouldBeNull();
        await coordinator.RollbackAsync(writer);
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Undo lifecycle: rejected foreign ended contexts cannot clean a colliding local scope")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task End_ForeignEndedMarkedContext_ShouldPreserveLocalScopes(bool commit)
    {
        // Arrange
        using var storage = new UndoTestStorage();
        using var foreignStorage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        await using var foreign = new TransactionCoordinator(foreignStorage, foreignStorage.Log, foreignStorage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var other = await foreign.BeginAsync(IsolationLevel.Snapshot);
        writer.Sequence.ShouldBe(other.Sequence);
        await coordinator.CreateUndoMarkerAsync(writer);
        await foreign.CreateUndoMarkerAsync(other);
        await foreign.CommitAsync(other);
        int ends = 0;
        storage.ScopeEnded = _ => ends++;
        // Act
        await Should.ThrowAsync<TransactionAbortedException>(() => commit
            ? coordinator.CommitAsync(other).AsTask() : coordinator.RollbackAsync(other).AsTask());
        // Assert
        ends.ShouldBe(0);
        coordinator.GetOpenContexts().ShouldHaveSingleItem().ShouldBeSameAs(writer);
        writer.State.ShouldBe(TransactionState.Active);
        await coordinator.ApplyStatementAsync(writer, _ => 1);
        await coordinator.CommitAsync(writer);
        ends.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo lifecycle: null end arguments preserve their argument failure and local scopes")]
    public async Task End_NullContext_ShouldPreserveArgumentFailureAndLocalScopes()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await coordinator.CreateUndoMarkerAsync(writer);
        // Act / Assert
        await Should.ThrowAsync<ArgumentNullException>(() => coordinator.CommitAsync(null!).AsTask());
        await Should.ThrowAsync<ArgumentNullException>(() => coordinator.RollbackAsync(null!).AsTask());
        coordinator.GetOpenContexts().ShouldHaveSingleItem().ShouldBeSameAs(writer);
        await coordinator.RollbackAsync(writer);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo streams: saturation cannot wrap the packed count or leak registration")]
    public async Task BeginWriteStream_SaturatedCount_ShouldRefuseWithoutWrapping()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        int registered = 0;
        try
        {
            for (; registered < 65_535; registered++) { writer.BeginWriteStream(); }
            // Act / Assert: exercise the counter without allocating 65,535 chunk buffers.
            Should.Throw<TransactionUndoException>(writer.BeginWriteStream).Code.ShouldBe("CK04011");
            writer.HasOpenWriteStreams.ShouldBeTrue();
            (await Should.ThrowAsync<TransactionUndoException>(() => coordinator.CreateUndoMarkerAsync(writer).AsTask())).Code.ShouldBe("CK04010");
        }
        finally { for (int i = 0; i < registered; i++) { writer.EndWriteStream(); } }
        writer.HasOpenWriteStreams.ShouldBeFalse();
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        await coordinator.ReleaseUndoMarkerAsync(writer, marker);
        await coordinator.CommitAsync(writer);
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Undo streams: ended transactions take precedence over stream saturation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BeginWriteStream_EndedSaturatedContext_ShouldReportAborted(bool commit)
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        int registered = 0;
        try
        {
            for (; registered < 65_535; registered++) { writer.BeginWriteStream(); }
            if (commit) { await coordinator.CommitAsync(writer); }
            else { await coordinator.RollbackAsync(writer); }
            // Act / Assert
            Should.Throw<TransactionAbortedException>(writer.BeginWriteStream);
            writer.HasOpenWriteStreams.ShouldBeTrue();
        }
        finally { for (int i = 0; i < registered; i++) { writer.EndWriteStream(); } }
        writer.HasOpenWriteStreams.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo lifecycle: failed creation unwinds its token and disposal ends model scopes outside the tracking lock")]
    public async Task CreateMarker_FailedModelRegistration_ShouldUnwindAndDisposeScopes()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        bool fail = true, active = false;
        int ends = 0;
        storage.ScopeChanged = value =>
        {
            if (value && fail) { fail = false; throw new System.IO.IOException("Injected scope registration failure"); }
            active = value;
        };
        storage.ScopeEnded = context => { context.State.ShouldBe(TransactionState.Faulted); ends++; };
        try
        {
            // Act
            await Should.ThrowAsync<System.IO.IOException>(() => coordinator.CreateUndoMarkerAsync(writer).AsTask());
            active.ShouldBeFalse();
            writer = coordinator.ReplaceIsolationLevel(writer, IsolationLevel.Snapshot);
            var marker = await coordinator.CreateUndoMarkerAsync(writer);
            Should.Throw<ArgumentException>(() => coordinator.ReplaceIsolationLevel(writer, IsolationLevel.Snapshot));
            await coordinator.ReleaseUndoMarkerAsync(writer, marker);
            // Assert: an unpublished earlier token would keep registration active.
            active.ShouldBeFalse();
            writer = coordinator.ReplaceIsolationLevel(writer, IsolationLevel.Snapshot);
            await coordinator.CreateUndoMarkerAsync(writer);
        }
        finally { await coordinator.DisposeAsync(); }
        ends.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo locks: every upgrade and re-acquisition restores the marker holding")]
    public async Task RollbackToMarker_AllModePairs_ShouldRestoreExactHolding()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var locks = coordinator.LockManager;
        var resource = LockResource.Entry(1, 1);
        // Act / Assert: include incomparable intent/update modes and unchanged re-grants.
        foreach (LockMode before in Enum.GetValues<LockMode>())
        {
            var outer = await coordinator.CreateUndoMarkerAsync(writer);
            await locks.AcquireAsync(writer.Sequence, resource, before);
            byte held = locks.HeldModes(writer.Sequence, resource);
            var marker = await coordinator.CreateUndoMarkerAsync(writer);
            foreach (LockMode after in Enum.GetValues<LockMode>())
            {
                await locks.AcquireAsync(writer.Sequence, resource, after);
                await locks.AcquireAsync(writer.Sequence, resource, after);
                await coordinator.RollbackToUndoMarkerAsync(writer, marker);
                locks.HeldModes(writer.Sequence, resource).ShouldBe(held);
            }
            await coordinator.RollbackToUndoMarkerAsync(writer, outer);
            locks.HeldModes(writer.Sequence, resource).ShouldBe((byte)0);
            await coordinator.ReleaseUndoMarkerAsync(writer, outer);
        }
        await coordinator.CommitAsync(writer);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo locks: releasing an inner marker transfers its grants and upgrade to its parent")]
    public async Task ReleaseMarker_NestedGrants_ShouldTransferToParent()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var locks = coordinator.LockManager;
        var retained = LockResource.Object(1);
        var added = LockResource.Entry(1, 3);
        await locks.AcquireAsync(writer.Sequence, retained, LockMode.Shared);
        byte original = locks.HeldModes(writer.Sequence, retained);
        var outer = await coordinator.CreateUndoMarkerAsync(writer);
        var inner = await coordinator.CreateUndoMarkerAsync(writer);
        await locks.AcquireAsync(writer.Sequence, retained, LockMode.Exclusive);
        await locks.AcquireAsync(writer.Sequence, added, LockMode.Exclusive);
        byte upgraded = locks.HeldModes(writer.Sequence, retained);
        // Act
        await coordinator.ReleaseUndoMarkerAsync(writer, inner);
        // Assert
        locks.HeldModes(writer.Sequence, retained).ShouldBe(upgraded);
        locks.HeldModes(writer.Sequence, added).ShouldNotBe((byte)0);
        await coordinator.RollbackToUndoMarkerAsync(writer, outer);
        locks.HeldModes(writer.Sequence, retained).ShouldBe(original);
        locks.HeldModes(writer.Sequence, added).ShouldBe((byte)0);
        await coordinator.ReleaseUndoMarkerAsync(writer, outer);
        var next = await coordinator.CreateUndoMarkerAsync(writer);
        await locks.AcquireAsync(writer.Sequence, retained, LockMode.Exclusive);
        await coordinator.ReleaseUndoMarkerAsync(writer, next);
        var last = await coordinator.CreateUndoMarkerAsync(writer);
        await coordinator.RollbackToUndoMarkerAsync(writer, last);
        locks.HeldModes(writer.Sequence, retained).ShouldBe(upgraded);
        await coordinator.CommitAsync(writer);
        locks.HeldModes(writer.Sequence, retained).ShouldBe((byte)0);
    }

    [Theory(DisplayName = "Cohesion Test [Transactions] - Undo locks: a released suffix or reverted upgrade wakes a waiter without ending the writer")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackToMarker_BlockedWaiter_ShouldProceed(bool upgrade)
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var other = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var resource = LockResource.Entry(1, 1);
        if (upgrade) { await coordinator.LockManager.AcquireAsync(writer.Sequence, resource, LockMode.Shared); }
        var marker = await coordinator.CreateUndoMarkerAsync(writer);
        await coordinator.LockManager.AcquireAsync(writer.Sequence, resource, LockMode.Exclusive);
        var waiting = coordinator.LockManager.AcquireAsync(other.Sequence, resource, LockMode.Shared).AsTask();
        waiting.IsCompleted.ShouldBeFalse();
        // Act
        await coordinator.RollbackToUndoMarkerAsync(writer, marker);
        await waiting.WaitAsync(_timeout);
        // Assert
        writer.State.ShouldBe(TransactionState.Active);
        coordinator.LockManager.HeldModes(writer.Sequence, resource).ShouldBe(upgrade ? (byte)1 : (byte)0);
        await coordinator.RollbackAsync(other);
        await coordinator.CommitAsync(writer);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo locks: deadlock edges follow a suffix release and later acquisition")]
    public async Task RollbackToMarker_DeadlockEdges_ShouldFollowCurrentHoldings()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var first = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var second = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var a = LockResource.Entry(1, 1);
        var b = LockResource.Entry(1, 2);
        var marker = await coordinator.CreateUndoMarkerAsync(first);
        await coordinator.LockManager.AcquireAsync(first.Sequence, a, LockMode.Exclusive);
        await coordinator.LockManager.AcquireAsync(second.Sequence, b, LockMode.Exclusive);
        var waiter = coordinator.LockManager.AcquireAsync(second.Sequence, a, LockMode.Exclusive).AsTask();
        await Should.ThrowAsync<TransactionDeadlockException>(() => coordinator.LockManager.AcquireAsync(first.Sequence, b, LockMode.Exclusive).AsTask());
        // Act
        await coordinator.RollbackToUndoMarkerAsync(first, marker);
        await waiter.WaitAsync(_timeout);
        // Assert: no stale edge to first after its grant disappears.
        await coordinator.RollbackAsync(second);
        await coordinator.LockManager.AcquireAsync(first.Sequence, b, LockMode.Exclusive);
        await coordinator.CommitAsync(first);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Undo locks: raced controls and waiter cancellation settle without lost grants")]
    public async Task RollbackToMarker_RacedReleaseAndCancellation_ShouldSettleEveryHolding()
    {
        // Arrange
        using var storage = new UndoTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var elapsed = Stopwatch.StartNew();
        int rounds = 0, controls = 0, refusals = 0, granted = 0, canceled = 0;
        // Act: keep running past the minimum until both waiter outcomes and controls ran.
        do
        {
            var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
            var other = await coordinator.BeginAsync(IsolationLevel.Snapshot);
            var resource = LockResource.Entry(1, (ulong)++rounds);
            var outer = await coordinator.CreateUndoMarkerAsync(writer);
            var inner = await coordinator.CreateUndoMarkerAsync(writer);
            await coordinator.LockManager.AcquireAsync(writer.Sequence, resource, LockMode.Exclusive);
            using var cancellation = new CancellationTokenSource();
            var waiting = coordinator.LockManager.AcquireAsync(other.Sequence, resource, LockMode.Shared, cancellation.Token).AsTask();
            Task release = DedicatedTestTask.Run(async () =>
            {
                try { await coordinator.ReleaseUndoMarkerAsync(writer, inner); Interlocked.Increment(ref controls); }
                catch (TransactionUndoException e) when (e.Code is "CK04008" or "CK04009") { Interlocked.Increment(ref refusals); }
            });
            if ((rounds & 1) == 0) { cancellation.Cancel(); }
            try { await coordinator.RollbackToUndoMarkerAsync(writer, outer); }
            catch (TransactionUndoException e) when (e.Code == "CK04009")
            { await release.WaitAsync(_timeout); await coordinator.RollbackToUndoMarkerAsync(writer, outer); }
            await release.WaitAsync(_timeout);
            try { await waiting.WaitAsync(_timeout); granted++; }
            catch (OperationCanceledException) { canceled++; }
            coordinator.LockManager.HeldModes(writer.Sequence, resource).ShouldBe((byte)0);
            await coordinator.RollbackAsync(other);
            await coordinator.CommitAsync(writer);
        }
        while ((elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || controls == 0 || refusals == 0 || granted == 0 || canceled == 0)
            && elapsed.Elapsed < _timeout);
        // Assert
        rounds.ShouldBeGreaterThan(1);
        controls.ShouldBeGreaterThan(0);
        refusals.ShouldBeGreaterThan(0);
        granted.ShouldBeGreaterThan(0);
        canceled.ShouldBeGreaterThan(0);
    }
}
