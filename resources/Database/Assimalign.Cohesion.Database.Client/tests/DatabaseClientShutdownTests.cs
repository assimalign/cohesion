using System;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client.Internal;

namespace Assimalign.Cohesion.Database.Client.Tests;

/// <summary>Pool shutdown coordinates returning sessions and parked renters.</summary>
[Collection(nameof(DatabaseClientEventSourceCollection))]
public sealed class DatabaseClientShutdownTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Client] - Shutdown: a return event cannot pool a session after shutdown drained the pool")]
    public async Task ReturnAsync_DisposedFromReturnEvent_ShouldCloseSession()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        long openBefore = DatabaseClientEventSource.Log.CurrentConnections;
        long rentedBefore = DatabaseClientEventSource.Log.CurrentRentedConnections;
        await using var harness = new StreamingClientTestHarness();
        var connection = await harness.Client.RentAsync(timeout.Token);
        Task? shutdown = null;
        using var listener = new ReturnShutdownListener(() => shutdown = harness.Client.DisposeAsync().AsTask());

        // Act: EventListener runs synchronously between return's initial check and idle push.
        await connection.DisposeAsync();
        _ = shutdown.ShouldNotBeNull();
        await shutdown.WaitAsync(timeout.Token);
        await harness.Client.DisposeAsync();

        // Assert: the connector really closed, rather than remaining open in an unreachable stack.
        listener.Returns.ShouldBe(1);
        listener.Closes.ShouldBe(1);
        DatabaseClientEventSource.Log.CurrentConnections.ShouldBe(openBefore);
        DatabaseClientEventSource.Log.CurrentRentedConnections.ShouldBe(rentedBefore);
        await Should.ThrowAsync<ObjectDisposedException>(async () => await harness.Client.RentAsync(timeout.Token));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Shutdown: parked renters fail as disposed without waiting for a rental to return")]
    public async Task DisposeAsync_WithParkedRenters_ShouldCompleteWithObjectDisposedException()
    {
        // Arrange: no caller cancellation can release these waits.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        long openBefore = DatabaseClientEventSource.Log.CurrentConnections;
        await using var harness = new StreamingClientTestHarness();
        await using var connection = await harness.Client.RentAsync(timeout.Token);
        Task<DatabaseConnection> first = harness.Client.RentAsync(CancellationToken.None).AsTask();
        Task<DatabaseConnection> second = harness.Client.RentAsync(CancellationToken.None).AsTask();
        first.IsCompleted.ShouldBeFalse();
        second.IsCompleted.ShouldBeFalse();

        // Act
        await harness.Client.DisposeAsync();

        // Assert: shutdown completes both waits while the original lease is still held.
        await Should.ThrowAsync<ObjectDisposedException>(async () => await first.WaitAsync(timeout.Token));
        await Should.ThrowAsync<ObjectDisposedException>(async () => await second.WaitAsync(timeout.Token));
        connection.IsOpen.ShouldBeTrue();
        await connection.DisposeAsync();
        connection.IsOpen.ShouldBeFalse();
        DatabaseClientEventSource.Log.CurrentConnections.ShouldBe(openBefore);
        harness.AcceptedConnections.ShouldBe(1);
    }

    private sealed class ReturnShutdownListener : EventListener
    {
        private readonly Action _shutdown;
        private int _returns;
        private int _closes;

        internal ReturnShutdownListener(Action shutdown)
        {
            _shutdown = shutdown;
            EnableEvents(DatabaseClientEventSource.Log, EventLevel.Verbose, EventKeywords.All);
        }

        internal int Returns => Volatile.Read(ref _returns);
        internal int Closes => Volatile.Read(ref _closes);

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (!ReferenceEquals(eventData.EventSource, DatabaseClientEventSource.Log))
            {
                return;
            }
            if (eventData.EventId == 6 && Interlocked.Increment(ref _returns) == 1)
            {
                _shutdown();
            }
            if (eventData.EventId == 3)
            {
                Interlocked.Increment(ref _closes);
            }
        }
    }
}
