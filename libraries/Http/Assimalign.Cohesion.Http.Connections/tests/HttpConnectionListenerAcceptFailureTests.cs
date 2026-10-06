using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Covers how the accept loops classify what escapes a transport listener's <c>AcceptAsync</c> (#1304).
/// A transport listener contains each connection's failure itself, so an exception that escapes is the
/// listener's own; only the HTTP listener's own cancellation ends a loop quietly. A cancellation the HTTP
/// listener did not request (once, a TLS handshake timing out inside the accept) faults the listener like
/// any other transport failure, rather than silently ending that endpoint's accepts.
/// </summary>
public class HttpConnectionListenerAcceptFailureTests
{
    // A hang guard: before #1304 the loop ended silently and the accept never completed.
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HttpConnectionListener: A stream transport's unrequested cancellation should fault the listener instead of silently ending its accepts")]
    public async Task AcceptOrListenAsync_OnUnrequestedCancellationFromStreamTransport_ShouldFaultTheListener()
    {
        // Arrange
        OperationCanceledException unrequested = new("The transport canceled its own accept.");
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new ThrowingConnectionListener(unrequested));
        await using HttpConnectionListener listener = new(options);

        // Act — the accept that observes the fault may see the backlog channel's own cancellation; the
        // listener's recorded failure is what every later accept reports.
        Exception? first = await Record.ExceptionAsync(() => WithinTestTimeoutAsync(listener.AcceptOrListenAsync()));
        Exception? later = await Record.ExceptionAsync(() => WithinTestTimeoutAsync(listener.AcceptOrListenAsync()));

        // Assert
        first.ShouldBeAssignableTo<OperationCanceledException>();
        later.ShouldBeSameAs(unrequested);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HttpConnectionListener: A multiplexed transport's unrequested cancellation should fault the listener instead of silently ending its accepts")]
    public async Task AcceptOrListenAsync_OnUnrequestedCancellationFromMultiplexedTransport_ShouldFaultTheListener()
    {
        // Arrange
        OperationCanceledException unrequested = new("The transport canceled its own accept.");
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new ThrowingMultiplexedConnectionListener(unrequested));
        await using HttpConnectionListener listener = new(options);

        // Act
        Exception? first = await Record.ExceptionAsync(() => WithinTestTimeoutAsync(listener.AcceptOrListenAsync()));
        Exception? later = await Record.ExceptionAsync(() => WithinTestTimeoutAsync(listener.AcceptOrListenAsync()));

        // Assert
        first.ShouldBeAssignableTo<OperationCanceledException>();
        later.ShouldBeSameAs(unrequested);
    }

    /// <summary>
    /// Awaits the accept, failing instead of hanging when it neither completes nor faults. Unlike
    /// <c>Task.WaitAsync</c>, this rethrows the accept's own cancellation exception rather than a new one.
    /// </summary>
    private static async Task<HttpConnection> WithinTestTimeoutAsync(Task<HttpConnection> accept)
    {
        if (await Task.WhenAny(accept, Task.Delay(_testTimeout)) != accept)
        {
            throw new TimeoutException("The accept neither completed nor faulted within the test timeout.");
        }

        return await accept;
    }
}
