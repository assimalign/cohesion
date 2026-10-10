using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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
/// the peer provokes counts toward the reset-flood budget like one it sends (CVE-2025-8671). A
/// response the send path completes gives its slot back at once, and a request an interceptor rejects
/// is not counted as a provoked reset.
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

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Stream Slots: A completed response should give its slot back before the after-response hooks run")]
    public async Task SendAsync_OnCompletedResponseWhileAfterResponseHookRuns_ShouldAdmitNextStream()
    {
        // Arrange — one concurrent stream is allowed, and an after-response hook keeps the exchange
        // running once its response is complete.
        GatedAfterResponseInterceptor hook = new();
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(hook);
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options, static http2 => http2.Limits.MaxStreamsPerConnection = 1);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/first"));
        IHttpContext first = await peer.ReceiveContextAsync();
        first.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("done"));

        // Act — the response completes; while the hook still runs, the client opens its next stream,
        // as one that keeps exactly MAX_CONCURRENT_STREAMS requests in flight does on END_STREAM.
        Task send = peer.ConnectionContext.SendAsync(first).AsTask();
        await hook.Entered.WaitAsync(_timeout);
        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/next"));
        await peer.SyncAsync();

        // Assert — RFC 9113 §5.1.2: stream 1 is closed for both sides, so it no longer counts.
        peer.Output.ForStream(1).ShouldContain(frame => frame.EndStream);
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.IsRstStream);
        peer.Output.ForStream(3).ShouldNotContain(frame => frame.IsRstStream);
        IHttpContext next = await peer.ReceiveContextAsync();
        next.Request.Path.Value.ShouldBe("/next");
        send.IsCompleted.ShouldBeFalse();

        hook.Release();
        await send.WaitAsync(_timeout);
        await peer.ConnectionContext.SendAsync(next).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Stream Slots: A stream reset while its handler runs should keep its slot even when its response already ended")]
    public async Task ReceiveAsync_OnPeerResetAfterStreamedHeadResponseEnded_ShouldRefuseNextStreamUntilExchangeEnds()
    {
        // Arrange — a HEAD response streamed through the raw sink ends the stream with its HEADERS frame
        // (RFC 9110 §9.3.2) while the handler that wrote it keeps running; one stream is allowed.
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options, static http2 => http2.Limits.MaxStreamsPerConnection = 1);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Request("HEAD", "/head"));
        IHttpContext head = await peer.ReceiveContextAsync();
        await head.Response.Streaming.FlushAsync().AsTask().WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsHeaders && frame.StreamId == 1 && frame.EndStream),
            "the HEAD response");

        // Act — the client resets the stream anyway, then opens another.
        await peer.SendRstStreamAsync(1, Http2ErrorCode.Cancel);
        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/next"));
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 3),
            "the refusal of stream 3");

        // Assert — an ended response frees the slot early only once the send path finishes it; this
        // handler still runs.
        peer.Output.ForStream(3).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.RefusedStream);

        // Act — the exchange ends.
        await peer.ConnectionContext.SendAsync(head).AsTask().WaitAsync(_timeout);
        await peer.SendHeadersAsync(5, endStream: true, Http2TestPeer.Get("/after"));
        IHttpContext after = await peer.ReceiveContextAsync();

        // Assert
        after.Request.Path.Value.ShouldBe("/after");
        await peer.ConnectionContext.SendAsync(after).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Stream Slots: Requests an interceptor rejects should not count toward the reset-flood budget")]
    public async Task ReceiveAsync_OnInterceptorRejectionsOverBudget_ShouldKeepConnection()
    {
        // Arrange — a budget of five resets per window, and an interceptor that rejects every request
        // head under /denied: the server's policy, decided before the request is dispatched.
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(new DeniedPathInterceptor());
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options, static http2 => http2.Limits.MaxResetStreamsPerWindow = 5);

        // Act — six rejected requests, then one the interceptor lets through.
        for (int index = 0; index < 6; index++)
        {
            await peer.SendHeadersAsync(1 + (2 * index), endStream: true, Http2TestPeer.Get($"/denied/{index}"));
        }

        await peer.SendHeadersAsync(13, endStream: true, Http2TestPeer.Get("/allowed"));
        IHttpContext allowed = await peer.ReceiveContextAsync();
        await peer.SyncAsync();

        // Assert — each rejected request is reset with CANCEL, and the connection still serves the next.
        allowed.Request.Path.Value.ShouldBe("/allowed");
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);
        peer.Output.Frames.Count(frame => frame.IsRstStream).ShouldBe(6);
        peer.Output.Frames.Where(frame => frame.IsRstStream).ShouldAllBe(frame => frame.GetRstStreamErrorCode() == Http2ErrorCode.Cancel);

        await peer.ConnectionContext.SendAsync(allowed).AsTask().WaitAsync(_timeout);
    }

    /// <summary>Rejects every request whose path starts with <c>/denied</c>, from its head hook.</summary>
    private sealed class DeniedPathInterceptor : HttpExchangeInterceptor
    {
        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;

        public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
        {
            if (context.Path.Value.StartsWith("/denied", StringComparison.Ordinal))
            {
                throw new HttpRequestRejectedException(HttpStatusCode.Forbidden);
            }
        }
    }

    /// <summary>
    /// A response interceptor whose after-response hook waits until the test releases it, so the
    /// exchange is still running after its response completed.
    /// </summary>
    private sealed class GatedAfterResponseInterceptor : HttpExchangeInterceptor
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Response;

        /// <summary>Completes once the first after-response hook is running.</summary>
        public Task Entered => _entered.Task;

        /// <summary>Lets every after-response hook return.</summary>
        public void Release() => _released.TrySetResult();

        public override async ValueTask AfterResponseAsync(HttpExchangeInterceptorResponseContext context, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _released.Task.ConfigureAwait(false);
        }
    }
}
