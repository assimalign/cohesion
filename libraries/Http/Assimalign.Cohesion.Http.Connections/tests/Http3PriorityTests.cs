using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal.Http3;
using Assimalign.Cohesion.Http.Connections.Internal.Http3.Frames;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// RFC 9218 §7.2 tests for the HTTP/3 PRIORITY_UPDATE handling: payload parsing,
/// the frame-type constants, application of a request-stream update to the
/// engine's observable priority state, rejection of a push update, and parsing
/// of the request Priority header.
/// </summary>
public class Http3PriorityTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3: PRIORITY_UPDATE payload decodes element id and priority")]
    [InlineData(0L, "u=1, i", 0L, 1, true)]
    [InlineData(4L, "u=7", 4L, 7, false)]
    [InlineData(8L, "", 8L, 3, false)]
    public void Http3PriorityUpdate_TryParse_ShouldDecodeElementIdAndPriority(
        long elementId, string fieldValue, long expectedId, int expectedUrgency, bool expectedIncremental)
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3PriorityUpdatePayload(elementId, fieldValue);

        Http3PriorityUpdate.TryParse(payload, out long parsedId, out HttpPriority priority).ShouldBeTrue();
        parsedId.ShouldBe(expectedId);
        priority.Urgency.ShouldBe(expectedUrgency);
        priority.Incremental.ShouldBe(expectedIncremental);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: PRIORITY_UPDATE with an empty payload fails to parse")]
    public void Http3PriorityUpdate_TryParse_EmptyPayload_ShouldFail()
    {
        Http3PriorityUpdate.TryParse(Array.Empty<byte>(), out _, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: PRIORITY_UPDATE frame types match RFC 9218")]
    public void Http3FrameType_PriorityUpdate_ShouldMatchRfc9218()
    {
        ((long)Http3FrameType.PriorityUpdateRequest).ShouldBe(0xF0700L);
        ((long)Http3FrameType.PriorityUpdatePush).ShouldBe(0xF0701L);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should set the effective priority from the request Priority header")]
    public async Task Http3_OnPriorityHeader_ShouldSetEffectivePriority()
    {
        // RFC 9218 §4 — the request's Priority header initialises the effective
        // priority, observable on the yielded HTTP/3 context.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp3Request(
            "GET", "/p", "https", "a",
            headers: new Dictionary<string, string> { ["priority"] = "u=2, i" });
        TestConnection stream = new(payload);
        TestMultiplexedConnection connection = new(stream);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        Http3Context http3Context = httpContext.ShouldBeOfType<Http3Context>();
        http3Context.EffectivePriority.ShouldBe(new HttpPriority(2, incremental: true));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should apply a control-stream PRIORITY_UPDATE to the referenced stream")]
    public async Task Http3_OnControlPriorityUpdateRequest_ShouldRecordStreamPriority()
    {
        // RFC 9218 §7.2 — a request-stream PRIORITY_UPDATE (0xF0700) on the
        // control stream is parsed and applied to the referenced stream's
        // observable engine priority. A request stream is queued after the
        // control stream so the background drain has time to process the frame.
        TestConnection control = new(
            HttpProtocolPayloadFactory.CreateHttp3ControlStreamWithControlFrames(
                new (long, long)[] { (0x01, 0) },
                ((long)Http3FrameType.PriorityUpdateRequest, HttpProtocolPayloadFactory.CreateHttp3PriorityUpdatePayload(0, "u=1, i"))),
            ConnectionDirection.ReadOnly);
        TestConnection request = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/g", "https", "a"));
        TestMultiplexedConnection connection = new(control, request);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.Request.Path.Value.ShouldBe("/g");

        Http3ConnectionContext context = httpConnectionContext.ShouldBeOfType<Http3ConnectionContext>();

        // Draining runs on a background task — poll until the frame is applied.
        HttpPriority recorded = default;
        bool found = false;
        for (int attempt = 0; attempt < 100 && !found; attempt++)
        {
            found = context.TryGetRequestStreamPriority(0, out recorded);
            if (!found)
            {
                await Task.Delay(10);
            }
        }

        found.ShouldBeTrue();
        recorded.ShouldBe(new HttpPriority(1, incremental: true));

        (await enumerator.MoveNextAsync()).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should reject a push PRIORITY_UPDATE on the control stream")]
    public async Task Http3_OnControlPriorityUpdatePush_ShouldReject()
    {
        // RFC 9218 §7.2 — the server issues no pushes, so a push PRIORITY_UPDATE
        // (0xF0701) references a push id that cannot exist; it is rejected.
        TestConnection control = new(
            HttpProtocolPayloadFactory.CreateHttp3ControlStreamWithControlFrames(
                new (long, long)[] { (0x01, 0) },
                ((long)Http3FrameType.PriorityUpdatePush, HttpProtocolPayloadFactory.CreateHttp3PriorityUpdatePayload(0, "u=1"))),
            ConnectionDirection.ReadOnly);
        TestConnection request = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/g", "https", "a"));
        TestMultiplexedConnection connection = new(control, request);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();

        Http3ConnectionContext context = httpConnectionContext.ShouldBeOfType<Http3ConnectionContext>();

        bool rejected = false;
        for (int attempt = 0; attempt < 100 && !rejected; attempt++)
        {
            rejected = context.PushPriorityUpdateRejected;
            if (!rejected)
            {
                await Task.Delay(10);
            }
        }

        rejected.ShouldBeTrue();

        (await enumerator.MoveNextAsync()).ShouldBeFalse();
    }

    private static async Task<IHttpContext> ReadSingleContextAsync(IHttpConnectionContext context)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }
}
