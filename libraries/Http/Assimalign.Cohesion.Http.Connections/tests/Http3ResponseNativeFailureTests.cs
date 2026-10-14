using System;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>Native QUIC failure codes identify peer loss before asynchronous closure notifications arrive.</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class Http3ResponseNativeFailureTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 Native Response Failure: A peer QUIC failure before closure notification should latch and cancel")]
    [InlineData(QuicError.ConnectionAborted)]
    [InlineData(QuicError.ConnectionTimeout)]
    [InlineData(QuicError.ConnectionIdle)]
    [InlineData(QuicError.TransportError)]
    public async Task ResponseAbortedByClient_OnNativePeerFailureBeforeClosure_ShouldLatchAndCancel(QuicError error)
    {
        // Arrange — the actual transport tokens remain open while the raw write throws, modeling
        // QuicStream's failed write winning the race against the driver's thread-pool continuation.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        await using IDuplexConnection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/stream", "https", "a"), timeout.Token);
        await request.Output.CompleteAsync();
        await using IHttpContext exchange = await peer.NextContextAsync();
        Http3Context context = (Http3Context)exchange;
        Http3ConnectionContext connection = (Http3ConnectionContext)peer.ConnectionContext;
        Http3ExchangeControl control = new(connection, context);
        QuicException nativeFailure = new(error, null, "The peer QUIC connection failed.");

        // Act
        OperationCanceledException? failure = null;
        try
        {
            await connection.RunResponseWriteAsync(context, nativeFailure,
                static (_, _, exception, _) => ValueTask.FromException(exception), timeout.Token);
        }
        catch (OperationCanceledException exception)
        {
            // Observe the actual cancellation: Shouldly's async assertion may synthesize a
            // TaskCanceledException and lose the native driver's InnerException.
            failure = exception;
        }

        // Assert
        failure.ShouldNotBeNull();
        control.ResponseAbortedByClient.ShouldBeTrue();
        context.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        failure.CancellationToken.ShouldBe(context.RequestCancelled);
        failure.InnerException.ShouldBeSameAs(nativeFailure);
        context.StreamConnection.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
        connection.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Native Response Failure: A local QUIC operation abort should not latch a client fault")]
    public async Task ResponseAbortedByClient_OnNativeLocalOperationAbort_ShouldRemainFalse()
    {
        // Arrange — this already-held behavior guards the classifier's local-abort exclusion.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        await using IDuplexConnection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/stream", "https", "a"), timeout.Token);
        await request.Output.CompleteAsync();
        await using IHttpContext exchange = await peer.NextContextAsync();
        Http3Context context = (Http3Context)exchange;
        Http3ConnectionContext connection = (Http3ConnectionContext)peer.ConnectionContext;
        Http3ExchangeControl control = new(connection, context);
        QuicException nativeFailure = new(QuicError.OperationAborted, null, "The local operation was aborted.");

        // Act
        QuicException failure = await Should.ThrowAsync<QuicException>(
            () => connection.RunResponseWriteAsync(context, nativeFailure,
                static (_, _, exception, _) => ValueTask.FromException(exception), timeout.Token).AsTask());

        // Assert
        failure.ShouldBeSameAs(nativeFailure);
        control.ResponseAbortedByClient.ShouldBeFalse();
        context.RequestCancelled.IsCancellationRequested.ShouldBeFalse();
    }
}
