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
/// Verifies the HTTP/3 minimum request-body data rate (#1085): a body the peer holds back past the grace
/// period fails the read, the request stream is stopped with <c>STOP_SENDING(H3_NO_ERROR)</c>
/// (RFC 9114 §4.1), and the exchange is answered <c>408</c> (RFC 9110 §15.5.9) while its response head is
/// uncommitted. A peer that keeps the rate is never rejected, and a CONNECT tunnel may idle.
/// </summary>
public class Http3RequestBodyDataRateTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);
    private static readonly HttpMinDataRate _rate = new(bytesPerSecond: 100, gracePeriod: TimeSpan.FromMilliseconds(300));

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Data Rate: A body held back should fail the read, stop the stream with H3_NO_ERROR, and be answered 408")]
    public async Task ReadBody_OnBodyBelowRate_ShouldStopWithNoErrorAndAnswer408()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(http3 => http3.Limits.MinRequestBodyDataRate = _rate);
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"));
        IHttpContext context = await peer.NextContextAsync();

        // Act — the application reads the body; the peer sends none of it.
        await Should.ThrowAsync<IOException>(() => context.Request.Body.ReadAsync(new byte[16]).AsTask().WaitAsync(_timeout));

        // Assert — the peer is asked to stop sending, with the code that promises a complete response
        // (it reaches the peer's writer on its next flush) …
        ConnectionResetException stop = await Should.ThrowAsync<ConnectionResetException>(() => request.Output.WriteAsync(new byte[1]).AsTask());
        stop.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.NoError);

        // … and that response is a 408, whatever the application staged.
        context.Response.StatusCode = HttpStatusCode.InternalServerError;
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));
        frames.Count.ShouldBe(1);
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames[0].Payload)[":status"].ShouldBe("408");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Data Rate: A body that keeps the rate should be read in full")]
    public async Task ReadBody_OnBodyAboveRate_ShouldDeliverWholeBody()
    {
        // Arrange — 50 octets every 100 ms is 500 octets per second, five times the rate, for longer than
        // the grace period.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(http3 => http3.Limits.MinRequestBodyDataRate = _rate);
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"));
        IHttpContext context = await peer.NextContextAsync();
        byte[] body = new byte[500];
        Random.Shared.NextBytes(body);
        Task<byte[]> received = ReadToEndAsync(context.Request.Body);

        // Act
        for (int offset = 0; offset < body.Length; offset += 50)
        {
            await Task.Delay(100);
            await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, body[offset..(offset + 50)]));
        }

        request.Output.Complete();

        // Assert
        (await received.WaitAsync(_timeout)).ShouldBe(body);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Data Rate: An idle extended CONNECT tunnel should not be held to the rate")]
    public async Task ReadBody_OnIdleExtendedConnectTunnel_ShouldNotApplyRate()
    {
        // Arrange — RFC 9110 §9.3.6: a CONNECT's DATA is tunnel traffic, which may idle indefinitely.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(http3 => http3.Limits.MinRequestBodyDataRate = _rate);
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "CONNECT"),
            (":protocol", "websocket"),
            (":scheme", "https"),
            (":path", "/chat"),
            (":authority", "api.test")));
        IHttpContext context = await peer.NextContextAsync();

        // Act — the tunnel idles for twice the grace period.
        Task<int> read = context.Request.Body.ReadAsync(new byte[16]).AsTask();
        await Task.Delay(_rate.GracePeriod * 2);

        // Assert — nothing was rejected, and the tunnel still carries the peer's octets.
        read.IsCompleted.ShouldBeFalse();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("ping")));
        (await read.WaitAsync(_timeout)).ShouldBe(4);
    }

    private static async Task<byte[]> ReadToEndAsync(Stream body)
    {
        using MemoryStream received = new();
        await body.CopyToAsync(received);
        return received.ToArray();
    }
}
