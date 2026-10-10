using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Tests;

/// <summary>
/// Covers how a layered listener (<c>listener.Use(layer)</c>) runs upgrades (#1304): each connection's
/// upgrade runs on its own task, a failed or stalled upgrade affects only its connection, the number of
/// connections held at once is bounded, and disposal releases every connection not yet returned.
/// </summary>
public class LayeredConnectionListenerTests
{
    // A hang guard, never a budget: every wait completes as soon as the listener does its part.
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Connections] - AcceptAsync: A stalled upgrade should not delay another connection")]
    public async Task AcceptAsync_WhileAnotherUpgradeStalls_ShouldReturnTheUpgradedConnection()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection stalled = new();
        TestConnection ready = new();
        ControlledConnectionLayer layer = new();
        layer.Hold(stalled);
        BlockingConnectionListener inner = new();
        inner.Enqueue(stalled);
        inner.Enqueue(ready);
        await using IConnectionListener listener = inner.Use(layer);

        // Act
        IConnection accepted = await listener.AcceptAsync(timeout.Token);

        // Assert
        accepted.ShouldBeSameAs(ready);
        stalled.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - AcceptAsync: A failed upgrade should close its connection and keep accepting")]
    public async Task AcceptAsync_WhenAnUpgradeFails_ShouldDisposeThatConnectionAndReturnTheNext()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection rejected = new();
        TestConnection ready = new();
        ControlledConnectionLayer layer = new();
        layer.Fail(rejected, new IOException("Not a handshake."));
        BlockingConnectionListener inner = new();
        inner.Enqueue(rejected);
        inner.Enqueue(ready);
        await using IConnectionListener listener = inner.Use(layer);

        // Act
        IConnection accepted = await listener.AcceptAsync(timeout.Token);

        // Assert
        accepted.ShouldBeSameAs(ready);
        await WhenClosedAsync(rejected, timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - AcceptAsync: Canceling a wait should leave the upgrade running")]
    public async Task AcceptAsync_WhenTheWaitIsCanceled_ShouldReturnTheConnectionToALaterCall()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using CancellationTokenSource abandon = new();
        TestConnection upgrading = new();
        ControlledConnectionLayer layer = new();
        layer.Hold(upgrading);
        BlockingConnectionListener inner = new();
        inner.Enqueue(upgrading);
        await using IConnectionListener listener = inner.Use(layer);
        Task<IConnection> abandoned = listener.AcceptAsync(abandon.Token).AsTask();
        await layer.WhenStarted(upgrading).WaitAsync(timeout.Token);

        // Act
        abandon.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => abandoned);
        layer.Release(upgrading);
        IConnection accepted = await listener.AcceptAsync(timeout.Token);

        // Assert
        accepted.ShouldBeSameAs(upgrading);
        upgrading.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - AcceptAsync: Should stop accepting from the inner listener at the upgrade limit")]
    public async Task AcceptAsync_AtTheUpgradeLimit_ShouldNotAcceptUntilASlotFrees()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection first = new();
        TestConnection second = new();
        ControlledConnectionLayer layer = new();
        layer.Hold(first);
        BlockingConnectionListener inner = new();
        inner.Enqueue(first);
        inner.Enqueue(second);
        await using IConnectionListener listener = inner.Use(layer, maxConcurrentUpgrades: 1);
        Task<IConnection> firstAccept = listener.AcceptAsync(timeout.Token).AsTask();
        await layer.WhenStarted(first).WaitAsync(timeout.Token);

        // Act — the one slot is held by the stalled upgrade. The pause gives a listener that ignored the
        // limit time to take the queued connection; it cannot make a correct listener fail.
        await Task.Delay(TimeSpan.FromMilliseconds(200), timeout.Token);
        int acceptsWhileFull = inner.AcceptCalls;
        bool secondStartedWhileFull = layer.WhenStarted(second).IsCompleted;
        layer.Release(first);
        IConnection firstAccepted = await firstAccept;
        IConnection secondAccepted = await listener.AcceptAsync(timeout.Token);

        // Assert
        acceptsWhileFull.ShouldBe(1);
        secondStartedWhileFull.ShouldBeFalse();
        firstAccepted.ShouldBeSameAs(first);
        secondAccepted.ShouldBeSameAs(second);
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - AcceptAsync: Should return upgrading connections before the inner listener's failure")]
    public async Task AcceptAsync_WhenTheInnerListenerFaults_ShouldReturnUpgradingConnectionsThenRethrow()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        InvalidOperationException failure = new("The endpoint failed.");
        TestConnection upgrading = new();
        ControlledConnectionLayer layer = new();
        layer.Hold(upgrading);
        BlockingConnectionListener inner = new();
        inner.Enqueue(upgrading);
        await using IConnectionListener listener = inner.Use(layer);
        Task<IConnection> firstAccept = listener.AcceptAsync(timeout.Token).AsTask();
        await layer.WhenStarted(upgrading).WaitAsync(timeout.Token);

        // Act
        inner.Fault(failure);
        layer.Release(upgrading);
        IConnection accepted = await firstAccept;
        InvalidOperationException observed = await Should.ThrowAsync<InvalidOperationException>(
            () => listener.AcceptAsync(timeout.Token).AsTask());

        // Assert
        accepted.ShouldBeSameAs(upgrading);
        observed.ShouldBeSameAs(failure);
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - DisposeAsync: Should close every connection the listener has not returned")]
    public async Task DisposeAsync_WithUpgradesPendingAndConnectionsQueued_ShouldDisposeEveryConnectionNotReturned()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection upgrading = new();
        TestConnection first = new();
        TestConnection second = new();
        ControlledConnectionLayer layer = new();
        layer.Hold(upgrading);
        BlockingConnectionListener inner = new();
        inner.Enqueue(upgrading);
        inner.Enqueue(first);
        inner.Enqueue(second);
        IConnectionListener listener = inner.Use(layer);
        IConnection returned = await listener.AcceptAsync(timeout.Token);
        TestConnection queued = ReferenceEquals(returned, first) ? second : first;
        await layer.WhenStarted(upgrading).WaitAsync(timeout.Token);
        await layer.WhenStarted(queued).WaitAsync(timeout.Token);

        // Act
        await listener.DisposeAsync();

        // Assert — the stalled upgrade was canceled and the queued connection drained; the returned one
        // belongs to its caller.
        inner.IsDisposed.ShouldBeTrue();
        upgrading.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        queued.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        returned.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
        await Should.ThrowAsync<ObjectDisposedException>(() => listener.AcceptAsync(timeout.Token).AsTask());
    }

    [Theory(DisplayName = "Cohesion Test [Connections] - Use: Should reject an upgrade limit below one")]
    [InlineData(0)]
    [InlineData(-1)]
    public void Use_WithNonPositiveUpgradeLimit_ShouldThrowArgumentOutOfRangeException(int maxConcurrentUpgrades)
    {
        // Arrange
        BlockingConnectionListener inner = new();
        ControlledConnectionLayer layer = new();

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => inner.Use(layer, maxConcurrentUpgrades));
    }

    private static async Task WhenClosedAsync(IConnection connection, CancellationToken cancellationToken)
    {
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = connection.ConnectionClosed.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            closed);

        await closed.Task.WaitAsync(cancellationToken);
    }
}
