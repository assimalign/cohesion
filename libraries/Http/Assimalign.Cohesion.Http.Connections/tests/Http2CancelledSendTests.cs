using System;
using System.Diagnostics;
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
/// An HTTP/2 send cancelled once its response is claimed (#1075). The stream can carry no other
/// response, so the transport resets it with <c>RST_STREAM(CANCEL)</c> (RFC 9113 §8.1, §6.4): the peer
/// learns the response will not complete, the stream's slot is free once the exchange ends, and the
/// connection's graceful close does not wait on it. The reset is written even when the caller's token
/// was already cancelled.
/// </summary>
public class Http2CancelledSendTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Cancelled Sends: Closing the connection should not wait for a stream whose send was cancelled")]
    public async Task GracefulCloseAsync_AfterSendCancelledMidResponse_ShouldNotWaitForStream()
    {
        // Arrange — a 10-octet stream window parks the buffered writer after its first DATA frame, and
        // the send is then cancelled.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(initialWindowSize: 10);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/slow"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Body = new MemoryStream(Http2TestPeer.CreateBody(100));

        using CancellationTokenSource cancellation = new();
        Task send = peer.ConnectionContext.SendAsync(exchange, cancellation.Token).AsTask();
        await peer.Output.ReadUntilAsync(_ => peer.Output.DataLength(1) >= 10, "the first window of DATA");
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => send.WaitAsync(_timeout));

        // Act
        Stopwatch elapsed = Stopwatch.StartNew();
        await ((Http2ConnectionContext)peer.ConnectionContext).GracefulCloseAsync().AsTask().WaitAsync(_timeout);
        elapsed.Stop();

        // Assert — the reset ended the exchange for the drain, which would otherwise wait out its
        // five-second window for a response that can never complete.
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1),
            "the reset of stream 1");
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.Cancel);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Cancelled Sends: A streamed response whose completion is cancelled should be reset with CANCEL")]
    public async Task SendAsync_OnCancelledCompletionOfStreamedResponse_ShouldResetStreamWithCancel()
    {
        // Arrange — the application streamed part of its response through the raw sink.
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/events"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        await exchange.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("partial")).AsTask().WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(_ => peer.Output.DataLength(1) >= 7, "the streamed DATA");

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        // Act — the send that would end the streamed response is cancelled before it writes END_STREAM.
        await Should.ThrowAsync<OperationCanceledException>(
            () => peer.ConnectionContext.SendAsync(exchange, cancellation.Token).AsTask().WaitAsync(_timeout));

        // Assert — the response can never end, so the stream is reset rather than left open.
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1),
            "the reset of stream 1");
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.Cancel);
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.EndStream);
        ((Http2ConnectionContext)peer.ConnectionContext).StreamCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Cancelled Sends: A reset requested with an already cancelled token should still be written")]
    public async Task SendAsync_OnCancelledExchangeWithCancelledToken_ShouldStillWriteReset()
    {
        // Arrange — a host gave up on the exchange: its stop budget ran out, so the token it resets the
        // exchange with is already cancelled.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/abandoned"));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        // Act
        exchange.Cancel();
        await peer.ConnectionContext.SendAsync(exchange, cancellation.Token).AsTask().WaitAsync(_timeout);

        // Assert — the reset still ends the stream for the peer.
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1),
            "the reset of stream 1");
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.Cancel);
        ((Http2ConnectionContext)peer.ConnectionContext).StreamCount.ShouldBe(0);
    }
}
