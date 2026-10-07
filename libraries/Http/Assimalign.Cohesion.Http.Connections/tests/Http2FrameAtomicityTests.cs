using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// HTTP/2 frame writes are atomic with respect to cancellation (#1326, RFC 9113 §4.1 / §6.10): a
/// cancelled write either never starts a frame or finishes it, and a HEADERS block with its
/// CONTINUATION frames goes out whole. A tap on the server's transport
/// (<see cref="Http2OutputTapConnection"/>) cancels the write's token the moment a frame's header has
/// reached the transport, which makes the cancellation land deterministically inside the frame write.
/// </summary>
public class Http2FrameAtomicityTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private const byte headersType = 0x1;
    private const byte continuationType = 0x9;
    private const byte endHeadersFlag = 0x4;

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Frame Writes: A streaming write cancelled after a DATA frame's header should leave the connection's framing intact")]
    public async Task WriteStreamingData_OnCancellationAfterFrameHeader_ShouldKeepTheFramingIntact()
    {
        // Arrange — the streamed response's head is committed; the tap cancels the write's token once the
        // header of stream 1's DATA frame reaches the transport.
        using CancellationTokenSource cancellation = new();
        Http2OutputTapConnection? tap = null;
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options, decorate: transport => tap = new Http2OutputTapConnection(transport));
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/stream"));
        IHttpContext streamed = await peer.ReceiveContextAsync();
        await streamed.Response.Streaming.FlushAsync().AsTask().WaitAsync(_timeout);
        tap!.OnFrameStarted = frame =>
        {
            if (frame.Type == Http2WireFrame.DataType && frame.StreamId == 1)
            {
                cancellation.Cancel();
            }
        };

        // Act — the write observes the cancellation; a second stream then answers on the same connection.
        await Should.ThrowAsync<OperationCanceledException>(() => streamed.Response.Streaming.WriteAsync(Http2TestPeer.CreateBody(512), cancellation.Token).AsTask());
        string other = await AnswerAnotherStreamAsync(peer, streamId: 3);

        // Assert — every frame still parses: the cancelled DATA frame went out whole, and the second
        // stream's response and a PING acknowledgement after it are read exactly.
        other.ShouldBe("intact");
        peer.Output.ForStream(1).Where(frame => frame.IsData).ShouldAllBe(frame => frame.Payload.Length == 512);

        await ResetAsync(peer, streamed);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Frame Writes: A head cancelled inside its HEADERS and CONTINUATION frames should go out as one whole header block")]
    public async Task WriteStreamingHeaders_OnCancellationInsideHeaderBlock_ShouldWriteTheWholeBlock()
    {
        // Arrange — a 40 KB field splits the streamed head into a HEADERS frame and CONTINUATION frames
        // (MAX_FRAME_SIZE is 16384); the tap cancels the commit's token once the HEADERS frame starts.
        using CancellationTokenSource cancellation = new();
        Http2OutputTapConnection? tap = null;
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options, decorate: transport => tap = new Http2OutputTapConnection(transport));
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/stream"));
        IHttpContext streamed = await peer.ReceiveContextAsync();
        streamed.Response.Headers[new HttpHeaderKey("x-large")] = new string('a', 40_000);
        tap!.OnFrameStarted = frame =>
        {
            if (frame.Type == headersType && frame.StreamId == 1)
            {
                cancellation.Cancel();
            }
        };

        // Act — the head commit observes the cancellation; a second stream then answers.
        await Should.ThrowAsync<OperationCanceledException>(() => streamed.Response.Streaming.FlushAsync(cancellation.Token).AsTask());
        string other = await AnswerAnotherStreamAsync(peer, streamId: 3);

        // Assert — RFC 9113 §6.10: the HEADERS frame is followed only by its own CONTINUATION frames, up
        // to END_HEADERS, before any other frame; the second stream's response is intact.
        other.ShouldBe("intact");
        IReadOnlyList<Http2WireFrame> frames = peer.Output.Frames;
        int head = frames.ToList().FindIndex(frame => frame.Type == headersType && frame.StreamId == 1);
        head.ShouldBeGreaterThanOrEqualTo(0);
        (frames[head].Flags & endHeadersFlag).ShouldBe(0);

        int index = head + 1;
        while ((frames[index - 1].Flags & endHeadersFlag) == 0)
        {
            frames[index].Type.ShouldBe(continuationType);
            frames[index].StreamId.ShouldBe(1);
            index++;
        }

        await ResetAsync(peer, streamed);
    }

    // Opens another stream, answers it with a small buffered response, and reads that response and a
    // PING acknowledgement after it back — which only succeeds while the connection's framing is intact.
    private static async Task<string> AnswerAnotherStreamAsync(Http2TestPeer peer, int streamId)
    {
        await peer.SendHeadersAsync(streamId, endStream: true, Http2TestPeer.Get("/other"));
        IHttpContext other = await peer.ReceiveContextAsync();
        other.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("intact"));
        await peer.ConnectionContext.SendAsync(other).AsTask().WaitAsync(_timeout);

        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsData && frame.StreamId == streamId && frame.EndStream),
            $"the response on stream {streamId}");
        await peer.SyncAsync();

        return Encoding.ASCII.GetString(peer.Output.DataPayload(streamId));
    }

    // Ends the exchange whose write was cancelled, the way a host does after a fault.
    private static async Task ResetAsync(Http2TestPeer peer, IHttpContext context)
    {
        await context.CancelAsync();
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
    }
}
