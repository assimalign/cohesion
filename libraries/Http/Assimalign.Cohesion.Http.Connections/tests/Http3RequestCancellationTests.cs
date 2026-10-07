using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// An HTTP/3 client that cancels a request in flight fires the exchange's
/// <see cref="IHttpContext.RequestCancelled"/> (#1329, RFC 9114 §4.1.1), as an HTTP/2 <c>RST_STREAM</c>
/// does: a <c>RESET_STREAM</c> on the request stream, a <c>STOP_SENDING</c> on the response, or both,
/// while the application neither reads the body nor writes the response. Driven over the in-memory
/// multiplexed driver, where the client completing its output with an error is its <c>RESET_STREAM</c>
/// and completing its input with an error is its <c>STOP_SENDING</c>; the real-QUIC counterpart is
/// <see cref="Http3RequestCancellationRoundTripTests"/>.
/// </summary>
public class Http3RequestCancellationTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    /// <summary>How the client cancels its request.</summary>
    public enum ClientCancellation
    {
        /// <summary><c>RESET_STREAM</c>: the client abandons the request direction.</summary>
        ResetStream,

        /// <summary><c>STOP_SENDING</c>: the client refuses the response direction.</summary>
        StopSending,

        /// <summary>Both directions at once.</summary>
        Abort,
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Cancellation: A client cancelling a request in flight should fire RequestCancelled")]
    [InlineData(ClientCancellation.ResetStream)]
    [InlineData(ClientCancellation.StopSending)]
    [InlineData(ClientCancellation.Abort)]
    public async Task RequestCancelled_OnClientCancellationInFlight_ShouldFire(ClientCancellation cancellation)
    {
        // Arrange — a POST whose body is still coming; the application has neither read nor answered it.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"));
        IHttpContext context = await peer.NextContextAsync();
        context.RequestCancelled.IsCancellationRequested.ShouldBeFalse();
        ConnectionAbortedException reason = new("The client cancelled the request (H3_REQUEST_CANCELLED).");

        // Act
        switch (cancellation)
        {
            case ClientCancellation.ResetStream:
                request.Output.Complete(reason);
                break;

            case ClientCancellation.StopSending:
                request.Input.Complete(reason);
                break;

            case ClientCancellation.Abort:
                request.Abort(reason);
                break;
        }

        // Assert
        await ShouldFireAsync(context.RequestCancelled);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Cancellation: A request the client ends and the server answers should not fire RequestCancelled")]
    public async Task RequestCancelled_OnCompletedExchange_ShouldNotFire()
    {
        // Arrange — a whole request: head, body, and the client's FIN.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"));
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Http2TestPeer.CreateBody(100)));
        await request.Output.CompleteAsync();
        IHttpContext context = await peer.NextContextAsync();

        // Act — the server reads the body to its end and answers; the client reads the response to its FIN.
        await context.Request.Body.ReadExactlyAsync(new byte[100]).AsTask().WaitAsync(_timeout);
        (await context.Request.Body.ReadAsync(new byte[1]).AsTask().WaitAsync(_timeout)).ShouldBe(0);
        context.Response.StatusCode = HttpStatusCode.Ok;
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        Http3FrameCollector output = new(request);
        await output.ReadUntilAsync(collector => collector.IsCompleted, "the end of the response");

        // Assert
        context.RequestCancelled.IsCancellationRequested.ShouldBeFalse();
    }

    private static async Task ShouldFireAsync(CancellationToken token)
    {
        TaskCompletionSource fired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = token.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), fired);

        try
        {
            await fired.Task.WaitAsync(_timeout);
        }
        catch (TimeoutException)
        {
            throw new ShouldAssertException("RequestCancelled did not fire after the client cancelled the request.");
        }
    }
}
