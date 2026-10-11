using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Verifies that HTTP/3 interim writers finish before the final head takes their request stream,
/// and that their flushes remain subject to the response's minimum data rate (#1428).
/// </summary>
public class Http3InterimResponseOrderingTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);
    private static readonly HttpMinDataRate _rate = new(1_000_000, TimeSpan.FromMilliseconds(300));

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 interim: A streamed final head should wait for an application 103 without sharing its writer")]
    public async Task WriteInterimResponseAsync_RacingStreamedFinalHead_ShouldFinishBeforeFinalHead()
    {
        // Arrange — the application 103 holds the request stream's flush while another task starts the final.
        using CancellationTokenSource cancellation = new(_timeout);
        await using Exchange exchange = await Exchange.OpenAsync(cancellation.Token);
        exchange.Writer.Stall();

        // Act
        Task interim = exchange.Interim.SendInterimResponseAsync(HttpStatusCode.EarlyHints, cancellationToken: cancellation.Token).AsTask();
        await WaitForAsync(() => exchange.Writer.IsFlushWaiting, cancellation.Token);
        Task head = exchange.Context.Response.Streaming.FlushAsync(cancellation.Token).AsTask();
        await WaitForAsync(() => exchange.Context.HasResponseStarted, cancellation.Token);
        bool finalWaited = !head.IsCompleted;
        exchange.Writer.Grant(1 << 20);
        await Task.WhenAll(interim, head).WaitAsync(cancellation.Token);
        await exchange.Peer.ConnectionContext.SendAsync(exchange.Context, cancellation.Token);

        // Assert
        finalWaited.ShouldBeTrue();
        exchange.Writer.ConcurrentUseObserved.ShouldBeFalse();
        (await ReadStatusesAsync(exchange.Request)).ShouldBe(["103", "200"]);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 interim: A large within-rate 103 should finish before the final head receives its own rate allowance")]
    public async Task WriteInterimResponseAsync_LargeWithinRateInterimRacingFinalHead_ShouldPreserveBothAllowances()
    {
        // Arrange — the 2 KB interim earns more than the grace period at 1,000 B/s.
        using CancellationTokenSource cancellation = new(_timeout);
        HttpMinDataRate rate = new(1_000, TimeSpan.FromMilliseconds(300));
        await using Exchange exchange = await Exchange.OpenAsync(cancellation.Token, rate: rate);
        HttpHeaderCollection hints = new();
        hints.Add("link", new string('a', 2_048));
        exchange.Writer.Stall();

        // Act — final waits longer than its own grace, but within the active interim's allowance.
        Task interim = exchange.Interim.SendInterimResponseAsync(HttpStatusCode.EarlyHints, hints, cancellation.Token).AsTask();
        await WaitForAsync(() => exchange.Writer.IsFlushWaiting, cancellation.Token);
        long interimOctets = exchange.Writer.Written;
        Task head = exchange.Context.Response.Streaming.FlushAsync(cancellation.Token).AsTask();
        await WaitForAsync(() => exchange.Context.HasResponseStarted, cancellation.Token);
        await Task.Delay(800, cancellation.Token);
        bool finalWaited = !head.IsCompleted;
        exchange.Writer.Grant(interimOctets);
        await interim.WaitAsync(cancellation.Token);

        // The final head must also get a fresh allowance once the interim releases the writer.
        await WaitForAsync(() => exchange.Writer.Written > interimOctets || head.IsCompleted, cancellation.Token);
        await Task.Delay(100, cancellation.Token);
        bool finalWaitedForOwnCredit = !head.IsCompleted;
        exchange.Writer.Grant(1 << 20);
        await head.WaitAsync(cancellation.Token);
        await exchange.Peer.ConnectionContext.SendAsync(exchange.Context, cancellation.Token);

        // Assert
        finalWaited.ShouldBeTrue();
        finalWaitedForOwnCredit.ShouldBeTrue();
        exchange.Context.RequestCancelled.IsCancellationRequested.ShouldBeFalse();
        exchange.ServerStream.WriteAbortCode.ShouldBeNull();
        exchange.Writer.ConcurrentUseObserved.ShouldBeFalse();
        (await ReadStatusesAsync(exchange.Request)).ShouldBe(["103", "200"]);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 interim: A final head waiting on an unbounded interim should honor caller and exchange cancellation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaimFinalResponseHeadAsync_CancelledWhileInterimStalled_ShouldCancelWithoutWriting(bool cancelExchange)
    {
        // Arrange — caller cancellation pins existing behavior; exchange cancellation must work independently.
        using CancellationTokenSource cancellation = new(_timeout);
        using CancellationTokenSource callerCancellation = new();
        await using Exchange exchange = await Exchange.OpenAsync(cancellation.Token);
        exchange.Writer.Stall();
        Task interim = exchange.Interim.SendInterimResponseAsync(HttpStatusCode.EarlyHints, cancellationToken: cancellation.Token).AsTask();
        await WaitForAsync(() => exchange.Writer.IsFlushWaiting, cancellation.Token);
        long interimOctets = exchange.Writer.Written;
        Task head = exchange.Context.Response.Streaming.FlushAsync(callerCancellation.Token).AsTask();
        await WaitForAsync(() => exchange.Context.HasResponseStarted, cancellation.Token);

        // Act
        if (cancelExchange)
        {
            exchange.Context.Cancel();
        }
        else
        {
            callerCancellation.Cancel();
        }

        OperationCanceledException failure;
        try
        {
            failure = await Should.ThrowAsync<OperationCanceledException>(() => head.WaitAsync(TimeSpan.FromSeconds(2), cancellation.Token));
        }
        finally
        {
            callerCancellation.Cancel();
            exchange.Writer.Grant(1 << 20);
            await interim.WaitAsync(cancellation.Token);
        }

        // Assert — cancellation releases the claim wait without writing a final head or resetting the stream.
        failure.CancellationToken.ShouldBe(cancelExchange ? exchange.Context.RequestCancelled : callerCancellation.Token);
        exchange.Writer.Written.ShouldBe(interimOctets);
        exchange.ServerStream.WriteAbortCode.ShouldBeNull();
        exchange.Writer.ConcurrentUseObserved.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 interim: An application 103 after the final head started should be refused cleanly")]
    public async Task WriteInterimResponseAsync_AfterStreamedFinalHeadStarted_ShouldThrowWithoutWriting()
    {
        // Arrange — this pins the existing final-first behavior, including a final flush still in flight.
        using CancellationTokenSource cancellation = new(_timeout);
        await using Exchange exchange = await Exchange.OpenAsync(cancellation.Token);
        exchange.Writer.Stall();
        Task head = exchange.Context.Response.Streaming.FlushAsync(cancellation.Token).AsTask();
        await WaitForAsync(() => exchange.Writer.IsFlushWaiting, cancellation.Token);
        long writtenBeforeInterim = exchange.Writer.Written;

        // Act
        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(() =>
            exchange.Interim.SendInterimResponseAsync(HttpStatusCode.EarlyHints, cancellationToken: cancellation.Token).AsTask());
        long writtenAfterInterim = exchange.Writer.Written;
        exchange.Writer.Grant(1 << 20);
        await head.WaitAsync(cancellation.Token);
        await exchange.Peer.ConnectionContext.SendAsync(exchange.Context, cancellation.Token);

        // Assert
        failure.Message.ShouldNotBeNullOrWhiteSpace();
        writtenAfterInterim.ShouldBe(writtenBeforeInterim);
        exchange.Writer.ConcurrentUseObserved.ShouldBeFalse();
        (await ReadStatusesAsync(exchange.Request)).ShouldBe(["200"]);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 interim: Queued interims should preserve the active writer's completion when the final head claims it")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteInterimResponseAsync_ConcurrentInterimAndFinal_ShouldPreserveActiveClaim(bool automaticContinue)
    {
        // Arrange — the second writer must not replace the completion source the final head waits on.
        using CancellationTokenSource cancellation = new(_timeout);
        await using Exchange exchange = await Exchange.OpenAsync(cancellation.Token, expectContinue: automaticContinue);
        exchange.Writer.Stall();
        Task first = exchange.Interim.SendInterimResponseAsync(HttpStatusCode.EarlyHints, cancellationToken: cancellation.Token).AsTask();
        await WaitForAsync(() => exchange.Writer.IsFlushWaiting, cancellation.Token);
        Task? second = null;
        Task<string>? read = null;
        using StreamReader bodyReader = new(exchange.Context.Request.Body, leaveOpen: true);

        // Act — another 103, or the automatic 100 from the first body read, queues behind the first 103.
        if (automaticContinue)
        {
            read = bodyReader.ReadToEndAsync(cancellation.Token);
        }
        else
        {
            second = exchange.Interim.SendInterimResponseAsync(HttpStatusCode.EarlyHints, cancellationToken: cancellation.Token).AsTask();
        }

        Task head = exchange.Context.Response.Streaming.FlushAsync(cancellationToken: cancellation.Token).AsTask();
        await WaitForAsync(() => exchange.Context.HasResponseStarted, cancellation.Token);
        exchange.Writer.Grant(1 << 20);
        await Task.WhenAll(first, head).WaitAsync(cancellation.Token);
        Exception? queuedFailure = null;

        if (automaticContinue)
        {
            await exchange.Request.Output.WriteAsync(
                HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("hello")), cancellation.Token);
            await exchange.Request.Output.CompleteAsync();
            (await read!.WaitAsync(cancellation.Token)).ShouldBe("hello");
        }
        else
        {
            try
            {
                await second!.WaitAsync(cancellation.Token);
            }
            catch (InvalidOperationException exception)
            {
                queuedFailure = exception;
            }
        }

        await exchange.Peer.ConnectionContext.SendAsync(exchange.Context, cancellation.Token);

        // Assert — the active 103 finishes, the final follows it, and the queued interim does not follow final.
        exchange.Writer.ConcurrentUseObserved.ShouldBeFalse();
        if (!automaticContinue)
        {
            queuedFailure.ShouldBeOfType<InvalidOperationException>();
        }

        (await ReadStatusesAsync(exchange.Request)).ShouldBe(["103", "200"]);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 interim: An application 103 denied stream credit should reset the request within its response rate allowance")]
    public async Task WriteInterimResponseAsync_NeverGrantedCredit_ShouldResetAndCancelExchange()
    {
        // Arrange — the application supplies no timeout shorter than the response rate's allowance.
        using CancellationTokenSource cancellation = new(_timeout);
        await using Exchange exchange = await Exchange.OpenAsync(cancellation.Token, rate: _rate);
        exchange.Writer.Stall();
        Task interim = exchange.Interim.SendInterimResponseAsync(HttpStatusCode.EarlyHints, cancellationToken: cancellation.Token).AsTask();
        Exception? failure = null;

        // Act — the pre-fix writer times out here; releasing credit also cleans up that failing run.
        try
        {
            await interim.WaitAsync(TimeSpan.FromSeconds(3), cancellation.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            failure = exception;
        }
        finally
        {
            exchange.Writer.Grant(1 << 20);
            try
            {
                await interim.WaitAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        // Assert
        OperationCanceledException cancelled = failure.ShouldBeAssignableTo<OperationCanceledException>()!;
        cancelled.CancellationToken.ShouldBe(exchange.Context.RequestCancelled);
        exchange.Context.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        exchange.ServerStream.WriteAbortCode.ShouldBe((long)Http3ErrorCode.RequestCancelled);
        ConnectionResetException reset = await Should.ThrowAsync<ConnectionResetException>(() => Http3InMemoryPeer.ReadToEndAsync(exchange.Request));
        reset.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.RequestCancelled);
        await exchange.Peer.ConnectionContext.SendAsync(exchange.Context, cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 interim: A request that disables the response rate should allow a delayed 103 and leave its rate configurable")]
    public async Task WriteInterimResponseAsync_RateDisabledForRequest_ShouldWaitWithoutFreezingRate()
    {
        // Arrange — this pins the request opt-out and the interim write's non-freezing rate lookup.
        using CancellationTokenSource cancellation = new(_timeout);
        await using Exchange exchange = await Exchange.OpenAsync(cancellation.Token, rate: _rate);
        exchange.Context.MinResponseDataRate!.MinDataRate = null;
        exchange.Writer.Stall();

        // Act
        Task interim = exchange.Interim.SendInterimResponseAsync(HttpStatusCode.EarlyHints, cancellationToken: cancellation.Token).AsTask();
        await Task.Delay(_rate.GracePeriod * 3, cancellation.Token);
        bool waitedForCredit = !interim.IsCompleted;
        exchange.Writer.Grant(1 << 20);
        await interim.WaitAsync(cancellation.Token);

        // Assert
        waitedForCredit.ShouldBeTrue();
        exchange.ServerStream.WriteAbortCode.ShouldBeNull();
        exchange.Context.RequestCancelled.IsCancellationRequested.ShouldBeFalse();
        exchange.Context.MinResponseDataRate!.IsReadOnly.ShouldBeFalse();
        exchange.Context.MinResponseDataRate!.MinDataRate = _rate;
        await exchange.Peer.ConnectionContext.SendAsync(exchange.Context, cancellation.Token);
        (await ReadStatusesAsync(exchange.Request)).ShouldBe(["103", "200"]);
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    private static async Task<string[]> ReadStatusesAsync(Connection request)
    {
        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));
        return frames.Where(frame => frame.FrameType == (long)Http3FrameType.Headers)
            .Select(frame => HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frame.Payload)[":status"])
            .ToArray();
    }

    private sealed class Exchange : IAsyncDisposable
    {
        private Exchange(Http3InMemoryPeer peer, Connection request, IHttpContext context, StallingConnection serverStream)
        {
            Peer = peer;
            Request = request;
            Context = context;
            ServerStream = serverStream;
        }

        public Http3InMemoryPeer Peer { get; }
        public Connection Request { get; }
        public IHttpContext Context { get; }
        public StallingConnection ServerStream { get; }
        public StallingPipeWriter Writer => ServerStream.Writer;
        public IHttpInterimResponseFeature Interim => Context.Features.Get<IHttpInterimResponseFeature>()!;

        public static async Task<Exchange> OpenAsync(CancellationToken cancellationToken, bool expectContinue = false, HttpMinDataRate? rate = null)
        {
            StallingMultiplexedConnection? server = null;
            Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
                options => options.Limits.MinResponseDataRate = rate,
                static options =>
                {
                    options.Interceptors.Add(HttpInterimResponses.CreateInterceptor());
                    options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
                    options.Interceptors.Add(HttpRequestLimits.CreateInterceptor());
                },
                inner => server = new StallingMultiplexedConnection(inner));

            try
            {
                Connection request = await peer.OpenRequestStreamAsync();
                Dictionary<string, string>? headers = expectContinue
                    ? new() { ["content-length"] = "5", ["expect"] = "100-continue" }
                    : null;
                await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request(
                    expectContinue ? "POST" : "GET", "/interim", "https", "a", headers: headers), cancellationToken);
                if (!expectContinue)
                {
                    await request.Output.CompleteAsync();
                }

                IHttpContext context = await peer.NextContextAsync();
                return new Exchange(peer, request, context, server!.RequestStreams.Single());
            }
            catch
            {
                await peer.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Writer.Grant(1 << 20);
            await Context.DisposeAsync();
            await Peer.DisposeAsync();
        }
    }
}
