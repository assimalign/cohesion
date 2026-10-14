using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

public class HttpExpectContinueCancellationTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Continue: A peer lost during the interim flush should cancel the body read with the exchange token")]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Http1_ReadAsync_OnPeerLossDuringInterimFlush_ShouldCancelWithExchangeToken(
        bool closeBeforeFailure, bool observeCancellation, bool passCallerToken)
    {
        // Arrange — the peer withholds its body while the first read flushes 100 Continue.
        byte[] head = Encoding.ASCII.GetBytes(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\n");
        TestConnection transport = new(head, completeInput: false);
        InterimFlushConnection output = new(transport);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(output), http1 => http1.Limits.MinRequestBodyDataRate = null);
        await using HttpConnectionListener listener = new(options);
        await using IHttpConnection connection = await listener.AcceptAsync();
        IHttpConnectionContext context = await connection.OpenAsync();
        await using IAsyncEnumerator<IHttpContext> contexts = context.ReceiveAsync().GetAsyncEnumerator();
        (await contexts.MoveNextAsync()).ShouldBeTrue();
        IHttpContext exchange = contexts.Current;

        // Act / Assert
        await VerifyPeerLossAsync(exchange, output, () => transport.Abort(new IOException("The peer disconnected.")),
            closeBeforeFailure, observeCancellation, passCallerToken);
        ((Http1RequestBodyStream)exchange.Request.Body).RejectedStatusCode.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Continue: A peer lost during the interim flush should cancel the body read with the exchange token")]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Http2_ReadAsync_OnPeerLossDuringInterimFlush_ShouldCancelWithExchangeToken(
        bool closeBeforeFailure, bool observeCancellation, bool passCallerToken)
    {
        // Arrange
        InterimFlushConnection? output = null;
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(
            configure: http2 => http2.Limits.MinRequestBodyDataRate = null,
            decorate: transport => output = new InterimFlushConnection(transport));
        await peer.SendHeadersAsync(1, endStream: false,
            Http2TestPeer.Request("POST", "/upload", ("content-length", "5"), ("expect", "100-continue")));
        Http2Context exchange = (Http2Context)await peer.ReceiveContextAsync();
        await peer.SyncAsync();

        // Act / Assert
        await VerifyPeerLossAsync(exchange, output.ShouldNotBeNull(), peer.Transport.CompleteInput,
            closeBeforeFailure, observeCancellation, passCallerToken);
        exchange.Stream.ClientFaultStatusCode.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Continue: A body fault racing the interim flush should retain precedence")]
    public async Task Http2_ReadAsync_OnBodyFaultDuringInterimFlush_ShouldKeepLatchedFailure()
    {
        // Arrange
        InterimFlushConnection? output = null;
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(
            decorate: transport => output = new InterimFlushConnection(transport));
        await peer.SendHeadersAsync(1, endStream: false,
            Http2TestPeer.Request("POST", "/upload", ("content-length", "5"), ("expect", "100-continue")));
        Http2Context exchange = (Http2Context)await peer.ReceiveContextAsync();
        await peer.SyncAsync();
        InterimFlushConnection writer = output.ShouldNotBeNull();
        writer.HoldFlush(observeCancellation: false);
        Task<int> pending = exchange.Request.Body.ReadAsync(new byte[1], CancellationToken.None).AsTask();
        await writer.FlushEntered.WaitAsync(_timeout);
        IOException bodyFailure = new("The body exceeded its configured limit.");

        // Act — a transport body rejection wins before the output reports its own failure.
        exchange.Stream.LatchClientFault(HttpStatusCode.RequestEntityTooLarge, bodyFailure);
        peer.Transport.CompleteInput();
        await exchange.RequestCancelled.WhenCancelledAsync();
        writer.FailFlush(new IOException("The interim flush failed."));

        // Assert
        IOException failure = await Should.ThrowAsync<IOException>(() => pending.WaitAsync(_timeout));
        failure.ShouldBeSameAs(bodyFailure);
        exchange.Stream.ClientFaultStatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Continue: Caller-only cancellation during the interim flush should preserve the caller token")]
    public async Task Http1_ReadAsync_OnCallerCancellationDuringInterimFlush_ShouldKeepCallerToken()
    {
        // Arrange — preserve the caller-only cancellation contract from before exchange linkage.
        byte[] head = Encoding.ASCII.GetBytes(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\n");
        TestConnection transport = new(head, completeInput: false);
        InterimFlushConnection output = new(transport);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(output), http1 => http1.Limits.MinRequestBodyDataRate = null);
        await using HttpConnectionListener listener = new(options);
        await using IHttpConnection connection = await listener.AcceptAsync();
        IHttpConnectionContext context = await connection.OpenAsync();
        await using IAsyncEnumerator<IHttpContext> contexts = context.ReceiveAsync().GetAsyncEnumerator();
        (await contexts.MoveNextAsync()).ShouldBeTrue();

        // Act / Assert
        await VerifyCallerCancellationAsync(contexts.Current, output, () => transport.Abort());
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Continue: Caller-only cancellation during the interim flush should preserve the caller token")]
    public async Task Http2_ReadAsync_OnCallerCancellationDuringInterimFlush_ShouldKeepCallerToken()
    {
        // Arrange — the independently cancelled caller must not cancel the exchange.
        InterimFlushConnection? output = null;
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(
            decorate: transport => output = new InterimFlushConnection(transport));
        await peer.SendHeadersAsync(1, endStream: false,
            Http2TestPeer.Request("POST", "/upload", ("content-length", "5"), ("expect", "100-continue")));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        await peer.SyncAsync();

        // Act / Assert
        await VerifyCallerCancellationAsync(exchange, output.ShouldNotBeNull(), peer.Transport.CompleteInput);
    }

    private static async Task VerifyCallerCancellationAsync(
        IHttpContext exchange, InterimFlushConnection output, Action disconnect)
    {
        using CancellationTokenSource caller = new();
        output.HoldFlush(observeCancellation: true);
        Task<int> pending = exchange.Request.Body.ReadAsync(new byte[1], caller.Token).AsTask();
        try
        {
            await output.FlushEntered.WaitAsync(_timeout);
            pending.IsCompleted.ShouldBeFalse();

            // Act
            caller.Cancel();

            // Assert
            OperationCanceledException failure = await Should.ThrowAsync<OperationCanceledException>(() => pending.WaitAsync(_timeout));
            failure.CancellationToken.ShouldBe(caller.Token);
            exchange.RequestCancelled.IsCancellationRequested.ShouldBeFalse();
        }
        finally
        {
            output.FailFlush(new IOException("Release the test's interim flush."));
            await pending.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            disconnect();
        }
    }

    private static async Task VerifyPeerLossAsync(
        IHttpContext exchange, InterimFlushConnection output, Action disconnect,
        bool closeBeforeFailure, bool observeCancellation, bool passCallerToken)
    {
        using CancellationTokenSource caller = new();
        CancellationToken exchangeToken = exchange.RequestCancelled;
        output.HoldFlush(observeCancellation);
        Task<int> pending = exchange.Request.Body.ReadAsync(new byte[1],
            passCallerToken ? caller.Token : CancellationToken.None).AsTask();
        await output.FlushEntered.WaitAsync(_timeout);
        pending.IsCompleted.ShouldBeFalse();

        // Act — model both the driver's closure-first and pipe-failure-first ordering.
        if (closeBeforeFailure)
        {
            disconnect();
            await exchangeToken.WhenCancelledAsync();
        }

        if (!observeCancellation)
        {
            output.FailFlush(new IOException("The peer disconnected during the interim flush."));
        }

        try
        {
            // Assert — a tokenless pending flush must wake too, and caller tokens cannot replace the exchange's.
            OperationCanceledException failure = await Should.ThrowAsync<OperationCanceledException>(() => pending.WaitAsync(_timeout));
            exchangeToken.IsCancellationRequested.ShouldBeTrue();
            failure.CancellationToken.ShouldBe(exchangeToken);
            caller.IsCancellationRequested.ShouldBeFalse();
        }
        finally
        {
            // Keep the intentionally broken baseline run from leaving a tokenless write pending.
            output.FailFlush(new IOException("Release the test's interim flush."));
            await pending.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            disconnect();
        }
    }
}
