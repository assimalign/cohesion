using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Client.Internal;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client.Tests;

/// <summary>Rental handles remain isolated after their pooled session changes owners.</summary>
[Collection(nameof(DatabaseClientEventSourceCollection))]
public sealed class DatabaseConnectionLeaseTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Creates the lease tests with per-test counter output.</summary>
    /// <param name="output">The test runner output.</param>
    public DatabaseConnectionLeaseTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Lease: stale members and disposal cannot affect the next renter")]
    public async Task RentAsync_AfterStaleHandleUseAndDisposal_ShouldPreserveNextRental()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = new StreamingClientTestHarness();
        long rentedBefore = DatabaseClientEventSource.Log.CurrentRentedConnections;
        using var listener = new RentalCounterListener();
        var stale = await harness.Client.RentAsync(timeout.Token);
        Task returned = stale.DisposeAsync().AsTask();
        await returned;
        await using var next = await harness.Client.RentAsync(timeout.Token);

        // Act / Assert: every member observes this lease's final state, including invalid arguments.
        next.ShouldNotBeSameAs(stale);
        stale.IsOpen.ShouldBeFalse();
        Should.Throw<ObjectDisposedException>(() => stale.Database);
        Should.Throw<ObjectDisposedException>(() => stale.Principal);
        Should.Throw<ObjectDisposedException>(() => stale.ServerVersion);
        Should.Throw<ObjectDisposedException>(() => stale.Family);
        Should.Throw<ObjectDisposedException>(() => stale.Capabilities);
        await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await stale.ExecuteAsync(StreamingClientTestHarness.CreatePing(), timeout.Token));
        await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await stale.ExecuteAsync<bool>(null!, timeout.Token));
        await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await stale.ExecuteStreamingAsync(StreamingClientTestHarness.CreateExchange(), timeout.Token));
        stale.AbortAsync().AsTask().ShouldBeSameAs(returned);
        await stale.AbortAsync();
        stale.DisposeAsync().AsTask().ShouldBeSameAs(returned);
        await stale.DisposeAsync();

        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        Task<DatabaseConnection> excess = harness.Client.RentAsync(waiting.Token).AsTask();
        excess.IsCompleted.ShouldBeFalse();
        waiting.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await excess);
        next.IsOpen.ShouldBeTrue();
        (await next.ExecuteAsync(StreamingClientTestHarness.CreatePing(), timeout.Token)).ShouldBe(ProtocolMessageType.Pong);
        harness.AcceptedConnections.ShouldBe(1);
        listener.Observed.ShouldBe([rentedBefore + 1, rentedBefore + 1]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Lease: late completed stream disposal cannot return a later rental")]
    public async Task ExecuteStreamingAsync_AfterReturnAndRerent_ShouldKeepLateCleanupOnOriginalLease()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = new StreamingClientTestHarness();
        var original = await harness.Client.RentAsync(timeout.Token);
        await using var stream = await original.ExecuteStreamingAsync(StreamingClientTestHarness.CreateExchange(), timeout.Token);
        harness.ContinueResponse.TrySetResult();
        await stream.CopyToAsync(Stream.Null, timeout.Token);
        await original.DisposeAsync();
        await using var next = await harness.Client.RentAsync(timeout.Token);

        // Act
        await stream.DisposeAsync();
        stream.Dispose();
        await original.DisposeAsync();

        // Assert
        harness.AcceptedConnections.ShouldBe(1);
        next.IsOpen.ShouldBeTrue();
        (await next.ExecuteAsync(StreamingClientTestHarness.CreatePing(), timeout.Token)).ShouldBe(ProtocolMessageType.Pong);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Client] - Lease: return joins active work and releases the slot even when cancellation callbacks throw")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DisposeAsync_DuringActiveExchange_ShouldJoinAndReleaseSlot(bool abort, bool throwingCallback)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = new StreamingClientTestHarness();
        var original = await harness.Client.RentAsync(timeout.Token);
        var exchange = new GatedExchange(throwingCallback, timeout.Token);
        Task<bool> running = original.ExecuteAsync(exchange, timeout.Token).AsTask();
        await exchange.Started.Task.WaitAsync(timeout.Token);
        Task returned = abort ? original.AbortAsync().AsTask() : original.DisposeAsync().AsTask();
        try
        {
            // Act: cancellation alone cannot release a connector whose exchange has not exited.
            await exchange.Cancelled.Task.WaitAsync(timeout.Token);
            returned.IsCompleted.ShouldBeFalse();
            original.DisposeAsync().AsTask().ShouldBeSameAs(returned);
            original.AbortAsync().AsTask().ShouldBeSameAs(returned);
            Task<DatabaseConnection> pending = harness.Client.RentAsync(timeout.Token).AsTask();
            pending.IsCompleted.ShouldBeFalse();
            exchange.Continue.TrySetResult();
            await Should.ThrowAsync<OperationCanceledException>(async () => await running);
            if (throwingCallback)
            {
                var failure = await Should.ThrowAsync<AggregateException>(async () => await returned);
                failure.InnerExceptions.ShouldContain(exception => exception is InvalidOperationException);
            }
            else
            {
                await returned.WaitAsync(timeout.Token);
            }
            await using var next = await pending;

            // Assert
            harness.AcceptedConnections.ShouldBe(2);
            (await next.ExecuteAsync(StreamingClientTestHarness.CreatePing(), timeout.Token)).ShouldBe(ProtocolMessageType.Pong);
        }
        finally
        {
            exchange.Continue.TrySetResult();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Lease: racing exchange admission and disposal never reaches the next owner")]
    public async Task DisposeAsync_RacingExchangeAdmission_ShouldIsolateEveryRental()
    {
        // Arrange: process-wide gauges are observed only in this non-parallel collection.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        long rentedBefore = DatabaseClientEventSource.Log.CurrentRentedConnections;
        await using var harness = new StreamingClientTestHarness();
        int admitted = 0;
        int refused = 0;
        int cancelled = 0;
        int cancellationInspections = 0;
        int callerCancellationInspections = 0;
        int staleRefused = 0;
        const int rounds = 256;
        var elapsed = Stopwatch.StartNew();

        // Deviates from the repo duration-based race rule per #1313 owner ruling: use fixed
        // rounds and verify each round; scheduler-dependent winner counts are reported only.
        // Act
        for (int round = 0; round < rounds; round++)
        {
            var original = await harness.Client.RentAsync(timeout.Token);
            using var callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var exchange = new CancellationExchange(() =>
            {
                _ = original.IsOpen;
                Interlocked.Increment(ref cancellationInspections);
                if (callerCancellation.IsCancellationRequested)
                {
                    Interlocked.Increment(ref callerCancellationInspections);
                }
            });
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool delayExecution = (round & 1) == 0;
            Task executing = Task.Run(async () =>
            {
                await start.Task.WaitAsync(timeout.Token);
                if (delayExecution)
                {
                    await Task.Yield();
                }
                try
                {
                    await original.ExecuteAsync(exchange, callerCancellation.Token);
                }
                catch (ObjectDisposedException)
                {
                    Interlocked.Increment(ref refused);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancelled);
                }
            }, timeout.Token);
            Task disposing = Task.Run(async () =>
            {
                await start.Task.WaitAsync(timeout.Token);
                if (!delayExecution)
                {
                    await Task.Yield();
                }
                await original.DisposeAsync();
            }, timeout.Token);
            Task cancelling = Task.Run(async () =>
            {
                await start.Task.WaitAsync(timeout.Token);
                callerCancellation.Cancel();
            }, timeout.Token);
            start.TrySetResult();
            await Task.WhenAll(executing, disposing, cancelling).WaitAsync(timeout.Token);
            if (exchange.Entered)
            {
                admitted++;
            }
            await using var next = await harness.Client.RentAsync(timeout.Token);
            Task stale = Task.Run(async () =>
            {
                await original.DisposeAsync();
                await original.AbortAsync();
                var probe = new CancellationExchange();
                await Should.ThrowAsync<ObjectDisposedException>(async () => await original.ExecuteAsync(probe, timeout.Token));
                probe.Entered.ShouldBeFalse();
                Interlocked.Increment(ref staleRefused);
            }, timeout.Token);
            (await next.ExecuteAsync(StreamingClientTestHarness.CreatePing(), timeout.Token)).ShouldBe(ProtocolMessageType.Pong);
            await stale;
            original.IsOpen.ShouldBeFalse();
            next.IsOpen.ShouldBeTrue();
            admitted.ShouldBe(cancelled);
            cancellationInspections.ShouldBe(admitted);
            (admitted + refused).ShouldBe(round + 1);
            staleRefused.ShouldBe(round + 1);
            DatabaseClientEventSource.Log.CurrentRentedConnections.ShouldBe(rentedBefore + 1);
        }

        // Assert: no duplicate return, missed release or stale execution can hide behind scheduling.
        _output.WriteLine($"Rounds={rounds}, admitted={admitted}, refused={refused}, cancelled={cancelled}, staleRefused={staleRefused}, cancellationInspections={cancellationInspections}, callerCancellationInspections={callerCancellationInspections}, elapsed={elapsed.Elapsed}.");
        cancelled.ShouldBe(admitted);
        cancellationInspections.ShouldBe(admitted);
        staleRefused.ShouldBe(rounds);
        rounds.ShouldBe(admitted + refused);
        DatabaseClientEventSource.Log.CurrentRentedConnections.ShouldBe(rentedBefore);
    }

    private sealed class RentalCounterListener : EventListener
    {
        private readonly ConcurrentQueue<long> _observed = new();

        /// <summary>Observes the gauge synchronously when each rental event is written.</summary>
        public RentalCounterListener()
        {
            EnableEvents(DatabaseClientEventSource.Log, EventLevel.Verbose, DatabaseClientEventSource.Keywords.Pool);
        }

        internal long[] Observed => _observed.ToArray();

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (ReferenceEquals(eventData.EventSource, DatabaseClientEventSource.Log) && eventData.EventId == 5)
            {
                _observed.Enqueue(DatabaseClientEventSource.Log.CurrentRentedConnections);
            }
        }
    }

    private sealed class CancellationExchange : DatabaseProtocolExchange<bool>
    {
        private readonly Action? _onCancellation;
        private int _entered;

        /// <summary>Creates an exchange that stays active until its rental is canceled.</summary>
        /// <param name="onCancellation">Optional inspection from the cancellation callback.</param>
        public CancellationExchange(Action? onCancellation = null) : base(StreamingClientTestHarness.Family)
        {
            _onCancellation = onCancellation;
        }

        internal bool Entered => Volatile.Read(ref _entered) != 0;

        protected override async ValueTask<bool> ExecuteCoreAsync(ProtocolFrameReader reader,
            ProtocolFrameWriter writer, CancellationToken cancellationToken)
        {
            Volatile.Write(ref _entered, 1);
            // The lease's source owns this registration. Keeping it until source disposal also
            // races linked-source cleanup against a caller callback that inspects the lease.
            if (_onCancellation is not null)
            {
                cancellationToken.Register(_onCancellation);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return true;
        }
    }

    private sealed class GatedExchange : DatabaseProtocolExchange<bool>
    {
        private readonly bool _throwingCallback;
        private readonly CancellationToken _timeout;

        /// <summary>Creates an exchange whose exit remains controlled after cancellation.</summary>
        /// <param name="throwingCallback">Whether its cancellation callback throws.</param>
        /// <param name="timeout">Bounds the test-controlled wait.</param>
        public GatedExchange(bool throwingCallback, CancellationToken timeout)
            : base(StreamingClientTestHarness.Family)
        {
            _throwingCallback = throwingCallback;
            _timeout = timeout;
        }

        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async ValueTask<bool> ExecuteCoreAsync(ProtocolFrameReader reader,
            ProtocolFrameWriter writer, CancellationToken cancellationToken)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                Cancelled.TrySetResult();
                if (_throwingCallback)
                {
                    throw new InvalidOperationException("Exchange cancellation callback failed.");
                }
            });
            Started.TrySetResult();
            await Continue.Task.WaitAsync(_timeout);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
    }
}
