using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// HTTP/2 stream slots under resets (#1072). A reset — the peer's <c>RST_STREAM</c>, or one the server
/// sends for a stream error the peer's frame raised — removes the stream at once, but the exchange it
/// carried runs until the host finalizes or disposes it. The stream keeps its slot against
/// <c>SETTINGS_MAX_CONCURRENT_STREAMS</c> until then (RFC 9113 §5.1.2, CVE-2023-44487), and a reset
/// the peer provokes counts toward the reset-flood budget like one it sends (CVE-2025-8671).
/// </summary>
public class Http2StreamSlotTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Stream Slots: Streams the peer reset should keep their slots until their exchanges end")]
    public async Task ReceiveAsync_OnPeerResetOfStreamsWhoseHandlersIgnoreCancellation_ShouldRefuseNextStreamUntilExchangesEnd()
    {
        // Arrange — two concurrent streams are allowed, and both handlers ignore cancellation: the
        // test neither finalizes nor disposes their exchanges while they run.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: static http2 => http2.Limits.MaxStreamsPerConnection = 2);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/slow/1"));
        IHttpContext first = await peer.ReceiveContextAsync();
        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/slow/3"));
        IHttpContext second = await peer.ReceiveContextAsync();

        // Act — the client resets both streams, then opens a third.
        await peer.SendRstStreamAsync(1, Http2ErrorCode.Cancel);
        await peer.SendRstStreamAsync(3, Http2ErrorCode.Cancel);
        await peer.SendHeadersAsync(5, endStream: true, Http2TestPeer.Get("/next"));
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 5),
            "the refusal of stream 5");

        // Assert — both handlers still run, so the third stream exceeds the cap.
        first.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        second.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        peer.Output.ForStream(5).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.RefusedStream);

        // Act — the first handler finishes: its send observes the reset and ends the exchange.
        await peer.ConnectionContext.SendAsync(first).AsTask().WaitAsync(_timeout);
        await peer.SendHeadersAsync(7, endStream: true, Http2TestPeer.Get("/after-send"));
        IHttpContext afterSend = await peer.ReceiveContextAsync();

        // Act — the second exchange is disposed without ever being sent.
        await second.DisposeAsync();
        await peer.SendHeadersAsync(9, endStream: true, Http2TestPeer.Get("/after-dispose"));
        IHttpContext afterDispose = await peer.ReceiveContextAsync();

        // Assert — each ended exchange gave its slot back.
        afterSend.Request.Path.Value.ShouldBe("/after-send");
        afterDispose.Request.Path.Value.ShouldBe("/after-dispose");
        peer.Output.ForStream(7).ShouldNotContain(frame => frame.IsRstStream);
        peer.Output.ForStream(9).ShouldNotContain(frame => frame.IsRstStream);

        await peer.ConnectionContext.SendAsync(afterSend).AsTask().WaitAsync(_timeout);
        await peer.ConnectionContext.SendAsync(afterDispose).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Stream Slots: A stream the server reset for a peer's stream error should keep its slot until its exchange ends")]
    public async Task ReceiveAsync_OnServerResetOfStreamWhoseHandlerIgnoresCancellation_ShouldRefuseNextStreamUntilExchangeEnds()
    {
        // Arrange — one concurrent stream is allowed, and its handler ignores cancellation.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: static http2 => http2.Limits.MaxStreamsPerConnection = 1);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/slow"));
        IHttpContext slow = await peer.ReceiveContextAsync();

        // Act — a zero-increment WINDOW_UPDATE is a stream error on a live stream (RFC 9113 §6.9), so
        // the server resets stream 1 itself; the client then opens another stream.
        await peer.SendWindowUpdateAsync(1, 0);
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1),
            "the server's reset of stream 1");
        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/next"));
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 3),
            "the refusal of stream 3");

        // Assert — the reset exchange still runs, so stream 3 exceeds the cap.
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.ProtocolError);
        peer.Output.ForStream(3).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.RefusedStream);
        slow.RequestCancelled.IsCancellationRequested.ShouldBeTrue();

        // Act — the handler finishes, and its send observes the reset.
        await peer.ConnectionContext.SendAsync(slow).AsTask().WaitAsync(_timeout);
        await peer.SendHeadersAsync(5, endStream: true, Http2TestPeer.Get("/after"));
        IHttpContext after = await peer.ReceiveContextAsync();

        // Assert
        after.Request.Path.Value.ShouldBe("/after");
        peer.Output.ForStream(5).ShouldNotContain(frame => frame.IsRstStream);

        await peer.ConnectionContext.SendAsync(after).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Stream Slots: Resets the peer provokes should count toward the reset-flood budget")]
    public async Task ReceiveAsync_OnServerResetsProvokedByPeerOverBudget_ShouldGoAwayEnhanceYourCalm()
    {
        // Arrange — a budget of five resets per window, and six live streams.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: static http2 => http2.Limits.MaxResetStreamsPerWindow = 5);

        for (int index = 0; index < 6; index++)
        {
            await peer.SendHeadersAsync(1 + (2 * index), endStream: true, Http2TestPeer.Get($"/item/{index}"));
            await peer.ReceiveContextAsync();
        }

        // Act — MadeYouReset (CVE-2025-8671): a zero-increment WINDOW_UPDATE on each stream makes the
        // server reset it, which ends the stream for the client as cheaply as its own RST_STREAM.
        for (int index = 0; index < 6; index++)
        {
            await peer.SendWindowUpdateAsync(1 + (2 * index), 0);
        }

        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsGoAway), "the GOAWAY");

        // Assert — the sixth provoked reset exceeds the budget.
        peer.Output.Frames.Single(frame => frame.IsGoAway).GetGoAwayErrorCode().ShouldBe(Http2ErrorCode.EnhanceYourCalm);
        peer.Output.Frames.Count(frame => frame.IsRstStream).ShouldBe(5);
    }
}
