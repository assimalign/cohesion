using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client.Tests;

/// <summary>Cancellation dispatch finishes before a rental releases its operation source.</summary>
[Collection(nameof(DatabaseClientEventSourceCollection))]
public sealed class DatabaseConnectionCancellationTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Client] - Lease: return joins queued cancellation callbacks before disposing their source")]
    public async Task DisposeAsync_BeforeQueuedCallbacksStart_ShouldJoinCallbacksBeforeSourceDisposalAsync()
    {
        // Arrange: the timeout is a hang guard; scheduling is controlled by two gates.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = new StreamingClientTestHarness();
        await using var original = await harness.Client.RentAsync(timeout.Token);
        int inspections = 0;
        var exchange = new InlineCancellationExchange(() =>
        {
            _ = original.IsOpen;
            Interlocked.Increment(ref inspections);
        });
        Task<bool> running = original.ExecuteAsync(exchange, timeout.Token).AsTask();
        await exchange.Started.Task.WaitAsync(timeout.Token);
        using var dispatch = new CancellationDispatchGate();
        Task returned;
        dispatch.EnableCapture();
        try
        {
            // CancelAsync captures this context for its callback-dispatch work item.
            returned = original.DisposeAsync().AsTask();
        }
        finally
        {
            dispatch.DisableCapture();
        }
        OperationCanceledException? cancellation = null;

        try
        {
            await dispatch.Entered.Task.WaitAsync(timeout.Token);
            exchange.OperationToken.IsCancellationRequested.ShouldBeTrue();

            // Act: this inline completion drains core -> connector -> lease cleanup until
            // its first suspension. The cancellation dispatcher still owns the other gate.
            exchange.AllowExit();

            // Assert: cleanup must wait for the queued callbacks rather than disposing
            // their source and silently removing the callback registration.
            running.IsCompleted.ShouldBeFalse("The admitted exchange must join cancellation dispatch before source disposal.");
            returned.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            // Never leave an ExecutionContext callback blocked, including on assertion failure.
            dispatch.Release();
            exchange.AllowExit();
            try
            {
                await running.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException exception) when (!timeout.IsCancellationRequested)
            {
                cancellation = exception;
            }
            await returned.WaitAsync(timeout.Token);
        }

        dispatch.TimedOut.ShouldBeFalse();
        cancellation.ShouldNotBeNull();
        inspections.ShouldBe(1);
        original.IsOpen.ShouldBeFalse();
        await using var next = await harness.Client.RentAsync(timeout.Token);
        (await next.ExecuteAsync(StreamingClientTestHarness.CreatePing(), timeout.Token)).ShouldBe(ProtocolMessageType.Pong);
        harness.AcceptedConnections.ShouldBe(2);
    }
}
