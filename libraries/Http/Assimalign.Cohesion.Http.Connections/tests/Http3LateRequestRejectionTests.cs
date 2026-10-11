using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// RFC 9114 §5.2 over the in-memory driver (#1306): once the server has sent its <c>GOAWAY</c>, a request
/// stream the client opens is at or above the announced boundary, so it is reset with
/// <c>H3_REQUEST_REJECTED</c> and never dispatched, while the request in flight still completes.
/// </summary>
public class Http3LateRequestRejectionTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 GOAWAY: A request stream opened after GOAWAY should be reset with H3_REQUEST_REJECTED and never dispatched")]
    public async Task BeginGracefulClose_OnRequestStreamOpenedAfterGoAway_ShouldRejectItUndispatched()
    {
        // Arrange — one request is dispatched, then the host begins a graceful close.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Task<IHttpContext> dispatch = peer.NextContextAsync();

        Connection inFlight = await peer.OpenRequestStreamAsync();
        await inFlight.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/in-flight", "https", "a"));
        inFlight.Output.Complete();
        IHttpContext exchange = await dispatch;

        peer.ConnectionContext.BeginGracefulClose();
        (await peer.ReadGoAwayAsync()).ShouldBe(4L);

        // Act — the client opens a request after the GOAWAY: stream 4, at the boundary.
        Connection late = await peer.OpenRequestStreamAsync();
        await late.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/late", "https", "a"));
        late.Output.Complete();

        // Assert — the late request is refused unprocessed, so its client may retry it elsewhere...
        ConnectionResetException rejection = await Should.ThrowAsync<ConnectionResetException>(() => Http3InMemoryPeer.ReadToEndAsync(late));
        rejection.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.RequestRejected);

        // ...never reaches the application, and the request in flight is still answered.
        (await peer.MoveNextAsync()).ShouldBeFalse();
        exchange.Request.Path.Value.ShouldBe("/in-flight");
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        (await Http3InMemoryPeer.ReadToEndAsync(inFlight)).ShouldNotBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 GOAWAY: Every request stream opened after GOAWAY should be rejected until the connection closes")]
    public async Task BeginGracefulClose_OnSeveralStreamsAfterGoAway_ShouldRejectEachOne()
    {
        // Arrange — an idle connection closes gracefully: the boundary is stream 0.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Task<bool> receive = peer.MoveNextAsync();
        peer.ConnectionContext.BeginGracefulClose();
        (await peer.ReadGoAwayAsync()).ShouldBe(0L);
        (await receive).ShouldBeFalse();

        // Act — three requests, one of which never sends its head.
        Connection silent = await peer.OpenRequestStreamAsync();
        Connection[] complete = new Connection[2];
        for (int index = 0; index < complete.Length; index++)
        {
            complete[index] = await peer.OpenRequestStreamAsync();
            await complete[index].Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", $"/late/{index}", "https", "a"));
            complete[index].Output.Complete();
        }

        // Assert — each is refused at once, whether or not its head arrived.
        foreach (Connection stream in complete.Prepend(silent))
        {
            ConnectionResetException rejection = await Should.ThrowAsync<ConnectionResetException>(() => Http3InMemoryPeer.ReadToEndAsync(stream));
            rejection.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.RequestRejected);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 GOAWAY: A rejected late stream should be cancelled on the QPACK decoder stream under its own stream ID")]
    public async Task BeginGracefulClose_WithDynamicTable_ShouldCancelTheLateStreamOnTheDecoderStream()
    {
        // Arrange — with the dynamic table enabled, a stream reset before its end owes a Stream Cancellation
        // (RFC 9204 §4.4.2), keyed on its wire ID: the late stream follows stream 0, so it is stream 4.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(static http3 =>
        {
            http3.QPack.MaxTableCapacity = 4096;
            http3.QPack.MaxBlockedStreams = 16;
        });
        Task<IHttpContext> dispatch = peer.NextContextAsync();

        Connection inFlight = await peer.OpenRequestStreamAsync();
        await inFlight.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/in-flight", "https", "a"));
        inFlight.Output.Complete();
        await dispatch;

        peer.ConnectionContext.BeginGracefulClose();
        (await peer.ReadGoAwayAsync()).ShouldBe(4L);
        Connection decoder = await peer.AcceptServerStreamAsync();

        // Act
        Connection late = await peer.OpenRequestStreamAsync();
        await late.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/late", "https", "a"));
        await Should.ThrowAsync<ConnectionResetException>(() => Http3InMemoryPeer.ReadToEndAsync(late));

        // Assert — the decoder stream's type (0x03), then the Stream Cancellation for stream 4 and nothing else.
        byte[] expected = [0x03, .. QPackDecoderInstructionEncoder.StreamCancellation(4)];
        (await ReadAtLeastAsync(decoder.Input, expected.Length)).ShouldBe(expected);
    }

    private static async Task<byte[]> ReadAtLeastAsync(PipeReader reader, int count)
    {
        using CancellationTokenSource timeout = new(_timeout);
        List<byte> received = new();

        while (received.Count < count)
        {
            ReadResult result = await reader.ReadAsync(timeout.Token);
            received.AddRange(result.Buffer.ToArray());
            reader.AdvanceTo(result.Buffer.End);

            if (result.IsCompleted)
            {
                break;
            }
        }

        return received.ToArray();
    }
}
