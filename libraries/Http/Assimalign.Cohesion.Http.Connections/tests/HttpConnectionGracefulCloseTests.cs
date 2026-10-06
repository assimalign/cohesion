using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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
/// <see cref="IHttpConnectionContext.BeginGracefulClose"/> on each protocol: the connection takes no new
/// exchange and announces the close (RFC 9112 §9.6, RFC 9113 §6.8, RFC 9114 §5.2), while the exchanges it
/// already yielded finish.
/// </summary>
public class HttpConnectionGracefulCloseTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Graceful close: Http1 answers the exchange in flight with Connection: close and then ends the connection")]
    public async Task BeginGracefulClose_Http1WithExchangeInFlight_ShouldAnswerWithConnectionCloseAndEndReceive()
    {
        // Arrange — a live connection (the peer could send another request) with one exchange in flight.
        TestConnection connection = new(
            HttpProtocolPayloadFactory.CreateHttp1Request("GET /work HTTP/1.1\r\nHost: api.test\r\n\r\n"),
            completeInput: false);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> exchanges = context.ReceiveAsync().GetAsyncEnumerator();
        (await exchanges.MoveNextAsync().AsTask().WaitAsync(_timeout)).ShouldBeTrue();
        IHttpContext exchange = exchanges.Current;

        // Act — the close begins while the exchange is still being served.
        context.BeginGracefulClose();
        exchange.Response.StatusCode = HttpStatusCode.Ok;
        exchange.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("finished"));
        await context.SendAsync(exchange);

        // Assert — the full response announces the close, and no further request is read.
        string response = Encoding.ASCII.GetString(await connection.ReadOutputAsync().WaitAsync(_timeout));
        response.ShouldStartWith("HTTP/1.1 200");
        response.ShouldContain("Connection: close", Case.Sensitive);
        response.ShouldEndWith("\r\n\r\nfinished");
        exchange.RequestCancelled.IsCancellationRequested.ShouldBeFalse();
        (await exchanges.MoveNextAsync().AsTask().WaitAsync(_timeout)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Graceful close: Http1 ends an idle keep-alive connection without a response")]
    public async Task BeginGracefulClose_Http1IdleKeepAlive_ShouldEndReceiveWithoutAResponse()
    {
        // Arrange — the first exchange was answered and kept alive; the receive loop now waits for the
        // first octet of a next request that never comes.
        TestConnection connection = new(
            HttpProtocolPayloadFactory.CreateHttp1Request("GET /first HTTP/1.1\r\nHost: api.test\r\n\r\n"),
            completeInput: false);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> exchanges = context.ReceiveAsync().GetAsyncEnumerator();
        (await exchanges.MoveNextAsync().AsTask().WaitAsync(_timeout)).ShouldBeTrue();
        exchanges.Current.Response.StatusCode = HttpStatusCode.Ok;
        await context.SendAsync(exchanges.Current);

        string first = Encoding.ASCII.GetString(await connection.ReadOutputAsync().WaitAsync(_timeout));
        first.ShouldNotContain("Connection: close", Case.Sensitive);

        Task<bool> next = exchanges.MoveNextAsync().AsTask();

        // Act
        context.BeginGracefulClose();

        // Assert — the idle wait ends at once; the receive sequence ends instead of timing out.
        (await next.WaitAsync(_timeout)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Graceful close: Http1 read timeout ends only the idle wait, never a request head that started")]
    public void CancelIdleWait_OnlyWhileIdle_ShouldReclaimTheConnectionWithoutAResponse()
    {
        // Arrange
        using Http1ReadTimeout idle = new(CancellationToken.None, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        using Http1ReadTimeout reading = new(CancellationToken.None, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        reading.OnRequestLineStarted();

        // Act
        idle.CancelIdleWait();
        idle.OnRequestLineStarted();
        reading.CancelIdleWait();

        // Assert — the idle read is reclaimed as an idle timeout (no 408); the started head keeps reading.
        idle.Token.IsCancellationRequested.ShouldBeTrue();
        idle.TimedOut.ShouldBeTrue();
        idle.IsHeadersPhase.ShouldBeFalse();
        reading.Token.IsCancellationRequested.ShouldBeFalse();
        reading.IsHeadersPhase.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Graceful close: Http2 sends GOAWAY with the last stream, refuses new streams, and completes the open stream")]
    public async Task BeginGracefulClose_Http2WithOpenStream_ShouldGoAwayRefuseNewStreamsAndCompleteTheOpenStream()
    {
        // Arrange — stream 1 is dispatched and still being served; the input stays live so the frame
        // pump keeps running through the close.
        TestConnection connection = new(
            HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/one", "https", "api.test"),
            completeInput: false);
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> exchanges = context.ReceiveAsync().GetAsyncEnumerator();
        (await exchanges.MoveNextAsync().AsTask().WaitAsync(_timeout)).ShouldBeTrue();
        IHttpContext exchange = exchanges.Current;
        Http2FrameCollector output = new(connection);

        // Act
        context.BeginGracefulClose();

        // Assert — GOAWAY(NO_ERROR) announces stream 1 as the last one processed, and the receive
        // sequence ends with nothing else queued.
        IReadOnlyList<Http2WireFrame> frames = await output.ReadUntilAsync(
            observed => observed.Any(frame => frame.IsGoAway),
            "the graceful GOAWAY");
        Http2WireFrame goAway = frames.Single(frame => frame.IsGoAway);
        goAway.GetGoAwayErrorCode().ShouldBe(Http2ErrorCode.NoError);
        BinaryPrimitives.ReadUInt32BigEndian(goAway.Payload).ShouldBe(1u);
        (await exchanges.MoveNextAsync().AsTask().WaitAsync(_timeout)).ShouldBeFalse();

        // A stream the peer opens after the close is refused, and is not yielded.
        byte[] second = HttpProtocolPayloadFactory.CreateHttp2Request(3, "GET", "/two", "https", "api.test");
        await connection.WriteInputAsync(second.AsSpan(24 + 9).ToArray());
        await output.ReadUntilAsync(
            observed => observed.Any(frame => frame.IsRstStream && frame.StreamId == 3),
            "the refusal of stream 3");
        output.ForStream(3).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.RefusedStream);

        // The open stream still completes with its full response.
        exchange.Response.StatusCode = HttpStatusCode.Ok;
        exchange.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("finished"));
        await context.SendAsync(exchange);
        await output.ReadUntilAsync(
            observed => observed.Any(frame => frame.StreamId == 1 && frame.EndStream),
            "the end of stream 1's response");
        output.ForStream(1).ShouldContain(frame => frame.IsHeaders);
        Encoding.ASCII.GetString(output.DataPayload(1)).ShouldBe("finished");
        output.ForStream(1).ShouldNotContain(frame => frame.IsRstStream);
        exchange.RequestCancelled.IsCancellationRequested.ShouldBeFalse();

        connection.CompleteInput();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Graceful close: Http2 cancels a fully received exchange when the receive token is cancelled")]
    public async Task ReceiveAsync_Http2CancelledWithFullyReceivedExchange_ShouldCancelTheExchange()
    {
        // Arrange — stream 1's request ended with its HEADERS (END_STREAM), so its body is complete.
        TestConnection connection = new(
            HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/one", "https", "api.test"),
            completeInput: false);
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();
        using CancellationTokenSource receive = new();
        await using IAsyncEnumerator<IHttpContext> exchanges = context.ReceiveAsync(receive.Token).GetAsyncEnumerator();
        (await exchanges.MoveNextAsync().AsTask().WaitAsync(_timeout)).ShouldBeTrue();
        IHttpContext exchange = exchanges.Current;
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        exchange.RequestCancelled.Register(() => cancelled.TrySetResult());

        // Act — the host gives up on the connection, as it does when its drain budget runs out.
        receive.Cancel();

        // Assert — the exchange observes cancellation, as on HTTP/1.1 and HTTP/3.
        await cancelled.Task.WaitAsync(_timeout);
        exchange.RequestCancelled.IsCancellationRequested.ShouldBeTrue();

        connection.CompleteInput();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Graceful close: Http3 sends GOAWAY with the first unaccepted stream and still answers the open request")]
    public async Task BeginGracefulClose_Http3WithOpenRequest_ShouldGoAwayAndStillAnswerTheRequest()
    {
        // Arrange — one request stream (client-bidi stream 0) dispatched and still being served.
        TestConnection request = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/g", "https", "a"));
        TestMultiplexedConnection connection = new(request);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnection httpConnection = await listener.AcceptOrListenAsync();
        IHttpConnectionContext context = await httpConnection.OpenAsync();
        await using IAsyncEnumerator<IHttpContext> exchanges = context.ReceiveAsync().GetAsyncEnumerator();
        (await exchanges.MoveNextAsync().AsTask().WaitAsync(_timeout)).ShouldBeTrue();
        IHttpContext exchange = exchanges.Current;

        // Act
        context.BeginGracefulClose();

        // Assert — before the connection is disposed, its control stream carries the GOAWAY: stream 0
        // may have been processed, stream 4 and above were not accepted.
        TestConnection controlStream = connection.ControlStream.ShouldNotBeNull();
        byte[] goAwayPayload = await ReadHttp3GoAwayAsync(controlStream);
        int index = 0;
        QuicVariableLengthInteger.Decode(goAwayPayload, ref index).ShouldBe(4L);
        (await exchanges.MoveNextAsync().AsTask().WaitAsync(_timeout)).ShouldBeFalse();

        // The open request is still answered in full.
        exchange.Response.StatusCode = HttpStatusCode.Ok;
        exchange.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("finished"));
        await context.SendAsync(exchange);
        IReadOnlyList<(long FrameType, byte[] Payload)> response = HttpProtocolPayloadFactory.ParseHttp3Frames(
            await request.ReadOutputAsync().WaitAsync(_timeout));
        response.ShouldContain(frame => frame.FrameType == (long)Http3FrameType.Headers);
        Encoding.ASCII.GetString(response.Single(frame => frame.FrameType == (long)Http3FrameType.Data).Payload).ShouldBe("finished");

        await httpConnection.DisposeAsync().AsTask().WaitAsync(_timeout);
    }

    private static async Task<byte[]> ReadHttp3GoAwayAsync(TestConnection controlStream)
    {
        List<byte> received = new();
        using CancellationTokenSource timeout = new(_timeout);

        while (true)
        {
            received.AddRange(await controlStream.ReadOutputAsync().WaitAsync(timeout.Token));

            IReadOnlyList<(long FrameType, byte[] Payload)> frames;
            try
            {
                (_, frames) = HttpProtocolPayloadFactory.ParseHttp3UnidirectionalStream(received.ToArray());
            }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                // A frame is split across reads; read more.
                continue;
            }

            foreach ((long frameType, byte[] payload) in frames)
            {
                if (frameType == (long)Http3FrameType.GoAway)
                {
                    return payload;
                }
            }
        }
    }
}
