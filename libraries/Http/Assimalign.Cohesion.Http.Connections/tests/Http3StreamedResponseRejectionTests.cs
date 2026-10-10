using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Verifies that an HTTP/3 request body the transport rejects after a streamed response head was
/// committed (#1084) resets the request stream with <c>H3_REQUEST_CANCELLED</c> (RFC 9114 §4.1.1)
/// instead of completing a response that looks whole — over the body-size cap (RFC 9110 §15.5.14) or
/// below the minimum data rate (§15.5.9, #1085) — as HTTP/2 resets the stream with <c>CANCEL</c>. A
/// streamed response that carries the rejection status itself still completes.
/// </summary>
public class Http3StreamedResponseRejectionTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Streamed Rejection: A body crossing the cap after a streamed response started should reset the stream with H3_REQUEST_CANCELLED")]
    public async Task SendAsync_OnBodyOverCapAfterStreamedResponseStarted_ShouldResetWithRequestCancelled()
    {
        // Arrange — a streamed response is on the wire before any of the body is read.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            http3 => http3.Limits.MaxRequestBodySize = 16,
            static options => options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor()));
        (Connection request, IHttpContext context) = await StartStreamedResponseAsync(peer);

        // Act — the body crosses the cap, the read fails, and the exchange is finalized.
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, new byte[64]));
        await Should.ThrowAsync<IOException>(() => context.Request.Body.ReadAsync(new byte[128]).AsTask().WaitAsync(_timeout));
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);

        // Assert — the response is reset, never completed with a FIN.
        ConnectionResetException reset = await Should.ThrowAsync<ConnectionResetException>(() => Http3InMemoryPeer.ReadToEndAsync(request));
        reset.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.RequestCancelled);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Streamed Rejection: A body held back after a streamed response started should reset the stream with H3_REQUEST_CANCELLED")]
    public async Task SendAsync_OnBodyBelowRateAfterStreamedResponseStarted_ShouldResetWithRequestCancelled()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            http3 => http3.Limits.MinRequestBodyDataRate = new HttpMinDataRate(bytesPerSecond: 100, gracePeriod: TimeSpan.FromMilliseconds(300)),
            static options => options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor()));
        (Connection request, IHttpContext context) = await StartStreamedResponseAsync(peer);

        // Act — the peer sends none of the body, so the read fails once the grace period runs out.
        await Should.ThrowAsync<IOException>(() => context.Request.Body.ReadAsync(new byte[128]).AsTask().WaitAsync(_timeout));
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);

        // Assert
        ConnectionResetException reset = await Should.ThrowAsync<ConnectionResetException>(() => Http3InMemoryPeer.ReadToEndAsync(request));
        reset.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.RequestCancelled);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Streamed Rejection: A streamed response carrying the rejection status itself should complete")]
    public async Task SendAsync_OnStreamedResponseCarryingRejectionStatus_ShouldComplete()
    {
        // Arrange — the application streams a 413 of its own.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            http3 => http3.Limits.MaxRequestBodySize = 16,
            static options => options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor()));
        (Connection request, IHttpContext context) = await StartStreamedResponseAsync(peer, HttpStatusCode.RequestEntityTooLarge);

        // Act
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, new byte[64]));
        await Should.ThrowAsync<IOException>(() => context.Request.Body.ReadAsync(new byte[128]).AsTask().WaitAsync(_timeout));
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);

        // Assert — the response is whole, and says why the body was refused.
        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames[0].Payload)[":status"].ShouldBe("413");
        Encoding.ASCII.GetString(frames[1].Payload).ShouldBe("partial");
    }

    private static async Task<(Connection Request, IHttpContext Context)> StartStreamedResponseAsync(
        Http3InMemoryPeer peer,
        HttpStatusCode? statusCode = null)
    {
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"));
        IHttpContext context = await peer.NextContextAsync();

        context.Response.StatusCode = statusCode ?? HttpStatusCode.Ok;
        IHttpResponseStreamingFeature streaming = context.Response.Streaming;
        await streaming.WriteAsync(Encoding.ASCII.GetBytes("partial"));
        await streaming.FlushAsync();
        return (request, context);
    }
}
