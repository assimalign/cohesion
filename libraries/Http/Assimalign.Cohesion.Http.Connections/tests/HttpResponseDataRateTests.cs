using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Verifies the minimum response data rate (#1384), the slow-read defence: a peer that grants no
/// flow-control credit, or accepts the response below the rate, releases its slot within its allowance
/// instead of holding the response writer. HTTP/1.1 closes the connection; HTTP/2 resets the stream with
/// <c>CANCEL</c>, or ends the connection with <c>GOAWAY(ENHANCE_YOUR_CALM)</c> when the connection-level
/// window is what stalls, or aborts it when its output stops draining; HTTP/3 resets the request stream
/// with <c>H3_REQUEST_CANCELLED</c>. Either way the exchange's <c>RequestCancelled</c> fires and the
/// stalled write throws an <see cref="OperationCanceledException"/>. A request can disable the rate for
/// its own response before the response starts, and a reader above the rate is never reclaimed.
/// </summary>
public class HttpResponseDataRateTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    // A fast rate keeps a writer's allowance near the grace period whatever it hands over, so a stall is
    // reclaimed in well under a second and a trickle well below the rate trips quickly.
    private static readonly HttpMinDataRate _rate = new(bytesPerSecond: 1_000_000, gracePeriod: TimeSpan.FromMilliseconds(300));

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Response Rate: A buffered response the peer never reads should close the connection and cancel the exchange")]
    public async Task Http1_OnBufferedResponseNeverRead_ShouldCloseConnectionAndCancelExchange()
    {
        // Arrange
        await using Http1Fixture fixture = await Http1Fixture.StartAsync(_rate);
        IHttpContext context = fixture.Context;
        context.Response.Body = new MemoryStream(new byte[64 * 1024]);
        fixture.Transport.Writer.Stall();

        // Act
        OperationCanceledException failure = await Should.ThrowAsync<OperationCanceledException>(
            () => fixture.Connection.SendAsync(context).AsTask().WaitAsync(_timeout));

        // Assert — HTTP/1.1 has no reset finer than the connection.
        fixture.Transport.IsAborted.ShouldBeTrue();
        context.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        failure.CancellationToken.ShouldBe(context.RequestCancelled);
        (await fixture.Receive.MoveNextAsync()).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Response Rate: A buffered response read below the rate should close the connection")]
    public async Task Http1_OnBufferedResponseReadBelowRate_ShouldCloseConnection()
    {
        // Arrange — the peer accepts 512 octets every 20 ms, about 25 KB/s against a 1 MB/s minimum.
        await using Http1Fixture fixture = await Http1Fixture.StartAsync(_rate);
        IHttpContext context = fixture.Context;
        context.Response.Body = new MemoryStream(new byte[256 * 1024]);
        fixture.Transport.Writer.Stall();
        using CancellationTokenSource trickle = new();
        Task granting = TrickleAsync(fixture.Transport.Writer, 512, TimeSpan.FromMilliseconds(20), trickle.Token);

        // Act
        Stopwatch elapsed = Stopwatch.StartNew();
        await Should.ThrowAsync<OperationCanceledException>(() => fixture.Connection.SendAsync(context).AsTask().WaitAsync(_timeout));
        await trickle.CancelAsync();
        await granting;

        // Assert — reclaimed long before the 10 s the body would take at the peer's pace.
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        fixture.Transport.IsAborted.ShouldBeTrue();
        context.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Response Rate: A streamed write the peer never reads should fail with the exchange's cancellation and close the connection")]
    public async Task Http1_OnStreamedWriteNeverRead_ShouldCancelHandlerAndCloseConnection()
    {
        // Arrange — the handler streams its response through the transport's sink.
        await using Http1Fixture fixture = await Http1Fixture.StartAsync(_rate, streaming: true);
        IHttpContext context = fixture.Context;
        fixture.Transport.Writer.Stall();

        // Act — the handler's own write is the one the stall holds.
        OperationCanceledException failure = await Should.ThrowAsync<OperationCanceledException>(
            () => context.Response.Streaming.WriteAsync(new byte[64 * 1024]).AsTask().WaitAsync(_timeout));

        // Assert
        failure.CancellationToken.ShouldBe(context.RequestCancelled);
        context.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        fixture.Transport.IsAborted.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Response Rate: A large streamed write the peer reads above the rate should complete past the grace period")]
    public async Task Http1_OnLargeStreamedWriteReadAboveRate_ShouldComplete()
    {
        // Arrange — a 100 KB/s minimum over a 300 ms grace. The handler writes 256 KB in one call and the
        // peer accepts 32 KB every 100 ms, 320 KB/s: the write takes about 0.8 s, past the grace period,
        // and is still above the rate, so the octets handed over must count toward the allowance.
        HttpMinDataRate rate = new(bytesPerSecond: 100_000, gracePeriod: TimeSpan.FromMilliseconds(300));
        await using Http1Fixture fixture = await Http1Fixture.StartAsync(rate, streaming: true);
        IHttpContext context = fixture.Context;
        await context.Response.Streaming.FlushAsync();
        fixture.Transport.Writer.Stall();
        using CancellationTokenSource trickle = new();
        Task granting = TrickleAsync(fixture.Transport.Writer, 32 * 1024, TimeSpan.FromMilliseconds(100), trickle.Token);

        // Act
        Stopwatch elapsed = Stopwatch.StartNew();
        await context.Response.Streaming.WriteAsync(new byte[256 * 1024]).AsTask().WaitAsync(_timeout);
        await trickle.CancelAsync();
        await granting;

        // Assert
        elapsed.Elapsed.ShouldBeGreaterThan(rate.GracePeriod);
        context.RequestCancelled.IsCancellationRequested.ShouldBeFalse();
        fixture.Transport.IsAborted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Response Rate: A response whose request disabled the rate should wait for a peer that does not read")]
    public async Task Http1_OnRateDisabledForRequest_ShouldWaitForPeer()
    {
        // Arrange
        await using Http1Fixture fixture = await Http1Fixture.StartAsync(_rate);
        IHttpContext context = fixture.Context;
        context.MinResponseDataRate!.MinDataRate.ShouldBeSameAs(_rate);
        context.MinResponseDataRate.MinDataRate = null;
        context.Response.Body = new MemoryStream(new byte[64 * 1024]);
        fixture.Transport.Writer.Stall();

        // Act — the peer reads nothing for three grace periods, then everything.
        Task send = fixture.Connection.SendAsync(context).AsTask();
        await Task.Delay(_rate.GracePeriod * 3);
        bool completedWhileStalled = send.IsCompleted;
        fixture.Transport.Writer.Grant(1L << 40);
        await send.WaitAsync(_timeout);

        // Assert
        completedWhileStalled.ShouldBeFalse();
        fixture.Transport.IsAborted.ShouldBeFalse();
        context.RequestCancelled.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Response Rate: The rate should be frozen when the response starts")]
    public async Task Http1_OnRateChangedAfterResponseStarted_ShouldThrow()
    {
        // Arrange
        await using Http1Fixture fixture = await Http1Fixture.StartAsync(_rate, streaming: true);
        IHttpContext context = fixture.Context;
        IHttpMinResponseDataRateFeature feature = context.MinResponseDataRate!;
        feature.IsReadOnly.ShouldBeFalse();

        // Act
        await context.Response.Streaming.FlushAsync().AsTask().WaitAsync(_timeout);

        // Assert
        feature.IsReadOnly.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => feature.MinDataRate = null);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Response Rate: A stream whose window the peer never opens should be reset with CANCEL and the connection keep serving")]
    public async Task Http2_OnStreamWindowNeverGranted_ShouldResetStreamWithCancel()
    {
        // Arrange — SETTINGS_INITIAL_WINDOW_SIZE = 0: the peer grants no stream credit at all.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(CreateOptions(), http2 => http2.Limits.MinResponseDataRate = _rate, initialWindowSize: 0);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/large"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Body = new MemoryStream(Http2TestPeer.CreateBody(1024));

        // Act
        OperationCanceledException failure = await Should.ThrowAsync<OperationCanceledException>(
            () => peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout));

        // Assert — a stream error (RFC 9113 §5.4.2) with CANCEL, which §7 defines for a stream that is no longer needed.
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1), "the reset of stream 1");
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.Cancel);
        exchange.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        failure.CancellationToken.ShouldBe(exchange.RequestCancelled);

        // The connection keeps serving: a bodyless response needs no credit.
        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/next"));
        IHttpContext next = await peer.ReceiveContextAsync();
        await peer.ConnectionContext.SendAsync(next).AsTask().WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsHeaders && frame.StreamId == 3 && frame.EndStream), "the response on stream 3");
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Response Rate: A stream whose window the peer opens below the rate should be reset with CANCEL")]
    public async Task Http2_OnStreamWindowGrantedBelowRate_ShouldResetStreamWithCancel()
    {
        // Arrange — the peer credits 16 octets every 50 ms, 320 B/s against a 1 MB/s minimum.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(CreateOptions(), http2 => http2.Limits.MinResponseDataRate = _rate, initialWindowSize: 0);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/large"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Body = new MemoryStream(Http2TestPeer.CreateBody(4096));
        Task send = peer.ConnectionContext.SendAsync(exchange).AsTask();

        // Act
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!send.IsCompleted && elapsed.Elapsed < _timeout)
        {
            await peer.SendWindowUpdateAsync(1, 16);
            await Task.WhenAny(send, Task.Delay(50));
        }

        // Assert — reclaimed long before the 13 s the body would take at the peer's pace.
        await Should.ThrowAsync<OperationCanceledException>(() => send.WaitAsync(_timeout));
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1), "the reset of stream 1");
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.Cancel);
        peer.Output.ForStream(1).ShouldContain(frame => frame.IsData);
        exchange.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Response Rate: A connection whose window the peer never reopens should end with GOAWAY(ENHANCE_YOUR_CALM)")]
    public async Task Http2_OnConnectionWindowNeverGranted_ShouldGoAway()
    {
        // Arrange — the stream window is wide open, but the connection-level window (65,535 octets, which
        // only a WINDOW_UPDATE on stream 0 refills) runs out: every stream of the connection is stalled.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(CreateOptions(), http2 => http2.Limits.MinResponseDataRate = _rate, initialWindowSize: 1 << 20);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/large"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Body = new MemoryStream(Http2TestPeer.CreateBody(100 * 1024));

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout));

        // Assert — RFC 9113 §10.5, and the receive enumeration ends.
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsGoAway), "the GOAWAY");
        peer.Output.Frames.First(frame => frame.IsGoAway).GetGoAwayErrorCode().ShouldBe(Http2ErrorCode.EnhanceYourCalm);
        peer.Output.DataLength(1).ShouldBe(65_535);
        exchange.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        await peer.WaitForReceiveEndAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Response Rate: A response the peer keeps crediting the connection for, below the rate, should reset its stream, not the connection")]
    public async Task Http2_OnConnectionWindowCreditedBelowRate_ShouldResetStreamNotConnection()
    {
        // Arrange — the stream window is wide open, and the peer keeps crediting the connection window, 1 KB
        // every 20 ms: about 50 KB/s against a 1 MB/s minimum. The peer is reading, so the connection is not
        // stalled; only this response falls below the rate.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(CreateOptions(), http2 => http2.Limits.MinResponseDataRate = _rate, initialWindowSize: 1 << 20);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/large"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Body = new MemoryStream(Http2TestPeer.CreateBody(256 * 1024));
        Task send = peer.ConnectionContext.SendAsync(exchange).AsTask();

        // Act
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!send.IsCompleted && elapsed.Elapsed < _timeout)
        {
            await peer.SendWindowUpdateAsync(0, 1024);
            await Task.WhenAny(send, Task.Delay(20));
        }

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(() => send.WaitAsync(_timeout));
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1), "the reset of stream 1");
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.Cancel);
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);
        exchange.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Response Rate: A connection whose output the peer stops reading should be aborted")]
    public async Task Http2_OnOutputNeverDrained_ShouldAbortConnection()
    {
        // Arrange — the windows are open, but the peer stops reading the connection itself (a full TCP
        // send buffer): the response's writes, and every writer queued behind them, would wait forever.
        StallingConnection? transport = null;
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(
            CreateOptions(),
            http2 => http2.Limits.MinResponseDataRate = _rate,
            initialWindowSize: 1 << 20,
            decorate: inner => transport = new StallingConnection(inner));
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/large"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        await peer.SyncAsync();
        exchange.Response.Body = new MemoryStream(Http2TestPeer.CreateBody(64 * 1024));
        transport!.Writer.Stall();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout));

        // Assert — no frame can get out any more, so the connection is aborted and the pump stops.
        transport.IsAborted.ShouldBeTrue();
        exchange.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        await peer.WaitForReceiveEndAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Response Rate: A PING acknowledgement the peer never reads should end the connection, not park a streamed response behind the write gate")]
    public async Task Http2_OnPingAckNeverDrainedBetweenStreamedFrames_ShouldAbortConnectionAndCancelHandler()
    {
        // Arrange — a streamed response is between DATA frames, every window open, when the peer stops
        // reading the connection and sends one PING, far under the flood limit. The pump takes the write
        // gate at control urgency for the acknowledgement, and the transport never accepts it.
        StallingConnection? transport = null;
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(
            CreateOptions(streaming: true),
            http2 => http2.Limits.MinResponseDataRate = _rate,
            initialWindowSize: 1 << 20,
            decorate: inner => transport = new StallingConnection(inner));
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/stream"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        await exchange.Response.Streaming.WriteAsync(new byte[1024]).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();
        transport!.Writer.Stall();
        await peer.SendAsync(Http2TestSettings.RawFrame(0x6, 0, 0, new byte[8]));
        await WaitForAsync(() => transport.Writer.IsFlushWaiting, "the pump's acknowledgement to wait on the transport");

        // Act — the handler's next frame queues for the write gate the pump holds.
        Stopwatch elapsed = Stopwatch.StartNew();
        OperationCanceledException failure = await Should.ThrowAsync<OperationCanceledException>(
            () => exchange.Response.Streaming.WriteAsync(new byte[1024]).AsTask().WaitAsync(_timeout));

        // Assert — the acknowledgement is held to the connection output's allowance, so the stall ends the
        // connection within it, and the handler queued behind the pump observes the cancellation.
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        transport.IsAborted.ShouldBeTrue();
        exchange.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        failure.CancellationToken.ShouldBe(exchange.RequestCancelled);
        await peer.WaitForReceiveEndAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Response Rate: A streamed write the peer grants no credit for should fail with the exchange's cancellation")]
    public async Task Http2_OnStreamedWriteNeverGranted_ShouldCancelHandler()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(CreateOptions(streaming: true), http2 => http2.Limits.MinResponseDataRate = _rate, initialWindowSize: 0);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/stream"));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        // Act
        OperationCanceledException failure = await Should.ThrowAsync<OperationCanceledException>(
            () => exchange.Response.Streaming.WriteAsync(new byte[1024]).AsTask().WaitAsync(_timeout));

        // Assert
        failure.CancellationToken.ShouldBe(exchange.RequestCancelled);
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1), "the reset of stream 1");
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.Cancel);
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Response Rate: A response whose request disabled the rate should wait for credit")]
    public async Task Http2_OnRateDisabledForRequest_ShouldWaitForCredit()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(CreateOptions(), http2 => http2.Limits.MinResponseDataRate = _rate, initialWindowSize: 0);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/large"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.MinResponseDataRate!.MinDataRate.ShouldBeSameAs(_rate);
        exchange.MinResponseDataRate.MinDataRate = null;
        exchange.Response.Body = new MemoryStream(Http2TestPeer.CreateBody(1024));

        // Act — the peer grants nothing for three grace periods, then the whole body.
        Task send = peer.ConnectionContext.SendAsync(exchange).AsTask();
        await Task.Delay(_rate.GracePeriod * 3);
        await peer.SyncAsync();
        bool completedWhileStalled = send.IsCompleted;
        bool resetWhileStalled = peer.Output.ForStream(1).Any(frame => frame.IsRstStream);
        await peer.SendWindowUpdateAsync(1, 1024);
        await send.WaitAsync(_timeout);

        // Assert
        completedWhileStalled.ShouldBeFalse();
        resetWhileStalled.ShouldBeFalse();
        await peer.Output.ReadUntilAsync(_ => peer.Output.DataLength(1) == 1024, "the whole response on stream 1");
        exchange.RequestCancelled.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Response Rate: A response the peer grants no stream credit for should be reset with H3_REQUEST_CANCELLED")]
    public async Task Http3_OnStreamCreditNeverGranted_ShouldResetWithRequestCancelled()
    {
        // Arrange — the peer grants no MAX_STREAM_DATA credit for the response.
        StallingMultiplexedConnection? server = null;
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            http3 => http3.Limits.MinResponseDataRate = _rate,
            static options => AddRequestLimits(options),
            inner => server = new StallingMultiplexedConnection(inner));
        (Connection request, IHttpContext context) = await OpenHttp3RequestAsync(peer);
        server!.RequestStreams.Single().Writer.Stall();
        context.Response.Body = new MemoryStream(new byte[64 * 1024]);

        // Act
        OperationCanceledException failure = await Should.ThrowAsync<OperationCanceledException>(
            () => peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout));

        // Assert — RFC 9114 §4.1.1: processed, then abandoned.
        server.RequestStreams.Single().WriteAbortCode.ShouldBe((long)Http3ErrorCode.RequestCancelled);
        ConnectionResetException reset = await Should.ThrowAsync<ConnectionResetException>(() => Http3InMemoryPeer.ReadToEndAsync(request));
        reset.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.RequestCancelled);
        context.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        failure.CancellationToken.ShouldBe(context.RequestCancelled);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Response Rate: A response the peer accepts below the rate should be reset with H3_REQUEST_CANCELLED")]
    public async Task Http3_OnStreamCreditGrantedBelowRate_ShouldResetWithRequestCancelled()
    {
        // Arrange — the peer grants 512 octets every 20 ms, about 25 KB/s against a 1 MB/s minimum.
        StallingMultiplexedConnection? server = null;
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            http3 => http3.Limits.MinResponseDataRate = _rate,
            static options => AddRequestLimits(options),
            inner => server = new StallingMultiplexedConnection(inner));
        (Connection request, IHttpContext context) = await OpenHttp3RequestAsync(peer);
        StallingPipeWriter writer = server!.RequestStreams.Single().Writer;
        writer.Stall();
        context.Response.Body = new MemoryStream(new byte[256 * 1024]);
        using CancellationTokenSource trickle = new();
        Task granting = TrickleAsync(writer, 512, TimeSpan.FromMilliseconds(20), trickle.Token);

        // Act
        Stopwatch elapsed = Stopwatch.StartNew();
        await Should.ThrowAsync<OperationCanceledException>(() => peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout));
        await trickle.CancelAsync();
        await granting;

        // Assert
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        server.RequestStreams.Single().WriteAbortCode.ShouldBe((long)Http3ErrorCode.RequestCancelled);
        context.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Response Rate: A streamed write the peer grants no credit for should fail with the exchange's cancellation")]
    public async Task Http3_OnStreamedWriteNeverGranted_ShouldCancelHandler()
    {
        // Arrange
        StallingMultiplexedConnection? server = null;
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            http3 => http3.Limits.MinResponseDataRate = _rate,
            static options => AddRequestLimits(options, streaming: true),
            inner => server = new StallingMultiplexedConnection(inner));
        (Connection request, IHttpContext context) = await OpenHttp3RequestAsync(peer);
        server!.RequestStreams.Single().Writer.Stall();

        // Act
        OperationCanceledException failure = await Should.ThrowAsync<OperationCanceledException>(
            () => context.Response.Streaming.WriteAsync(new byte[1024]).AsTask().WaitAsync(_timeout));

        // Assert
        failure.CancellationToken.ShouldBe(context.RequestCancelled);
        server.RequestStreams.Single().WriteAbortCode.ShouldBe((long)Http3ErrorCode.RequestCancelled);
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Response Rate: A response whose request disabled the rate should wait for credit")]
    public async Task Http3_OnRateDisabledForRequest_ShouldWaitForCredit()
    {
        // Arrange
        StallingMultiplexedConnection? server = null;
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            http3 => http3.Limits.MinResponseDataRate = _rate,
            static options => AddRequestLimits(options),
            inner => server = new StallingMultiplexedConnection(inner));
        (Connection request, IHttpContext context) = await OpenHttp3RequestAsync(peer);
        StallingPipeWriter writer = server!.RequestStreams.Single().Writer;
        writer.Stall();
        context.MinResponseDataRate!.MinDataRate.ShouldBeSameAs(_rate);
        context.MinResponseDataRate.MinDataRate = null;
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("payload"));

        // Act — the peer grants nothing for three grace periods, then everything.
        Task send = peer.ConnectionContext.SendAsync(context).AsTask();
        await Task.Delay(_rate.GracePeriod * 3);
        bool completedWhileStalled = send.IsCompleted;
        writer.Grant(1L << 40);
        await send.WaitAsync(_timeout);

        // Assert
        completedWhileStalled.ShouldBeFalse();
        server.RequestStreams.Single().WriteAbortCode.ShouldBeNull();
        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));
        Encoding.ASCII.GetString(frames[1].Payload).ShouldBe("payload");
    }

    private static async Task<(Connection Request, IHttpContext Context)> OpenHttp3RequestAsync(Http3InMemoryPeer peer)
    {
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/large", "https", "a"));
        request.Output.Complete();
        IHttpContext context = await peer.NextContextAsync();
        return (request, context);
    }

    private static HttpConnectionListenerOptions CreateOptions(bool streaming = false)
    {
        HttpConnectionListenerOptions options = new();
        AddRequestLimits(options, streaming);
        return options;
    }

    private static void AddRequestLimits(HttpConnectionListenerOptions options, bool streaming = false)
    {
        options.Interceptors.Add(HttpRequestLimits.CreateInterceptor());

        if (streaming)
        {
            options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string description)
    {
        Stopwatch elapsed = Stopwatch.StartNew();

        while (!condition())
        {
            elapsed.Elapsed.ShouldBeLessThan(_timeout, $"Timed out waiting for {description}.");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Grants <paramref name="octets"/> of credit every <paramref name="interval"/> until cancelled: a peer
    /// that reads, but slowly.
    /// </summary>
    private static async Task TrickleAsync(StallingPipeWriter writer, int octets, TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken);
                writer.Grant(octets);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// An HTTP/1.1 connection whose output the test stalls, with one request (a <c>GET</c>) received.
    /// </summary>
    private sealed class Http1Fixture : IAsyncDisposable
    {
        private readonly HttpConnectionListener _listener;

        private Http1Fixture(StallingConnection transport, HttpConnectionListener listener, IHttpConnectionContext connection, IAsyncEnumerator<IHttpContext> receive)
        {
            Transport = transport;
            _listener = listener;
            Connection = connection;
            Receive = receive;
        }

        public StallingConnection Transport { get; }

        public IHttpConnectionContext Connection { get; }

        public IAsyncEnumerator<IHttpContext> Receive { get; }

        public IHttpContext Context => Receive.Current;

        public static async Task<Http1Fixture> StartAsync(HttpMinDataRate? rate, bool streaming = false)
        {
            TestConnection inner = new(Encoding.ASCII.GetBytes("GET /large HTTP/1.1\r\nHost: api.test\r\n\r\n"), completeInput: false);
            StallingConnection transport = new(inner);
            HttpConnectionListenerOptions options = CreateOptions(streaming);
            options.UseHttp1(new TestConnectionListener(transport), http1 => http1.Limits.MinResponseDataRate = rate);

            HttpConnectionListener listener = new(options);
            IHttpConnectionContext connection = await (await listener.AcceptOrListenAsync()).OpenAsync();
            IAsyncEnumerator<IHttpContext> receive = connection.ReceiveAsync().GetAsyncEnumerator();
            (await receive.MoveNextAsync().AsTask().WaitAsync(_timeout)).ShouldBeTrue();

            return new Http1Fixture(transport, listener, connection, receive);
        }

        public async ValueTask DisposeAsync()
        {
            await Receive.DisposeAsync();
            await _listener.DisposeAsync();
        }
    }
}
