using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>HTTP/1.1 response failures expose their client origin through the exchange control.</summary>
public class Http1ResponseClientFaultTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Response Client Fault: A slow reader should latch before cancellation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseAbortedByClient_OnSlowReader_ShouldLatchBeforeCancellation(bool streaming)
    {
        // Arrange
        await using Fixture fixture = await Fixture.StartAsync(new HttpMinDataRate(1_000_000, TimeSpan.FromMilliseconds(100)));
        fixture.Transport.Writer.Stall();
        bool latchedWhenCancelled = false;
        using CancellationTokenRegistration registration = fixture.Context.RequestCancelled.Register(
            () => latchedWhenCancelled = fixture.Control.ResponseAbortedByClient);

        // Act
        OperationCanceledException failure = await Should.ThrowAsync<OperationCanceledException>(
            () => fixture.WriteAsync(streaming).WaitAsync(TimeSpan.FromSeconds(10)));

        // Assert
        fixture.Control.ResponseAbortedByClient.ShouldBeTrue();
        latchedWhenCancelled.ShouldBeTrue();
        failure.CancellationToken.ShouldBe(fixture.Context.RequestCancelled);
        fixture.Control.ClientFaultStatusCode.ShouldBeNull();
        fixture.Transport.IsAborted.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Response Client Fault: A lost connection should release a pending response write")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ResponseAbortedByClient_OnConnectionLostDuringWrite_ShouldLatchAndCancel(bool streaming, bool gated)
    {
        // Arrange
        await using Fixture fixture = await Fixture.StartAsync(gated ? new HttpMinDataRate(1, TimeSpan.FromMinutes(1)) : null);
        fixture.Transport.Writer.Stall();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task pending = fixture.WriteAsync(streaming);
        while (!fixture.Transport.Writer.IsFlushWaiting)
        {
            await Task.Delay(1, timeout.Token);
        }

        // Act — the driver reports that the peer reset the underlying connection.
        fixture.Transport.Abort(new ConnectionResetException("Peer reset."));
        OperationCanceledException failure;
        try
        {
            failure = await Should.ThrowAsync<OperationCanceledException>(() => pending.WaitAsync(timeout.Token));
        }
        finally
        {
            fixture.Transport.Writer.Grant(1024 * 1024);
        }

        // Assert
        fixture.Control.ResponseAbortedByClient.ShouldBeTrue();
        fixture.Control.ClientFaultStatusCode.ShouldBeNull();
        fixture.Context.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        failure.CancellationToken.ShouldBe(fixture.Context.RequestCancelled);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Response Client Fault: Application cancellation should leave the latch unset")]
    public async Task ResponseAbortedByClient_OnApplicationCancellation_ShouldRemainFalse()
    {
        // Arrange — this companion guards existing application cancellation behavior.
        await using Fixture fixture = await Fixture.StartAsync(null);
        await fixture.Context.Response.Streaming.WriteAsync(new byte[1]);

        // Act
        fixture.Context.Cancel();
        await fixture.Connection.SendAsync(fixture.Context);

        // Assert
        fixture.Context.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        fixture.Control.ResponseAbortedByClient.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Response Client Fault: A caller cancelling a pending write should retain its token without a client-fault latch")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WriteAsync_OnCallerCancellation_ShouldPreserveCallerToken(bool streaming, bool gated)
    {
        // Arrange — retaining the caller's token guards the behavior preceding response-token linking.
        await using Fixture fixture = await Fixture.StartAsync(gated ? new HttpMinDataRate(1, TimeSpan.FromMinutes(1)) : null);
        fixture.Transport.Writer.Stall();
        using CancellationTokenSource caller = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task pending = fixture.WriteAsync(streaming, caller.Token);
        while (!fixture.Transport.Writer.IsFlushWaiting)
        {
            await Task.Delay(1, timeout.Token);
        }

        // Act
        caller.Cancel();
        OperationCanceledException failure;
        try
        {
            failure = await Should.ThrowAsync<OperationCanceledException>(() => pending.WaitAsync(timeout.Token));
        }
        finally
        {
            fixture.Transport.Writer.Grant(1024 * 1024);
        }

        // Assert
        failure.CancellationToken.ShouldBe(caller.Token);
        fixture.Context.RequestCancelled.IsCancellationRequested.ShouldBeFalse();
        fixture.Control.ResponseAbortedByClient.ShouldBeFalse();
        fixture.Transport.IsAborted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Response Client Fault: Host shutdown closing the driver should leave the response-client latch clear")]
    public async Task WriteAsync_OnHostShutdownAndDriverAbort_ShouldLeaveLatchClear()
    {
        // Arrange — the server's expired drain budget cancels ReceiveAsync and aborts the driver.
        using CancellationTokenSource host = new();
        await using Fixture fixture = await Fixture.StartAsync(null, host.Token);
        fixture.Transport.Writer.Stall();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task pending = fixture.WriteAsync(streaming: true);
        while (!fixture.Transport.Writer.IsFlushWaiting)
        {
            await Task.Delay(1, timeout.Token);
        }
        using CancellationTokenRegistration shutdown = host.Token.Register(() => fixture.Transport.Abort(
            new ConnectionAbortedException("The server's stop budget expired.")));

        // Act — this registration runs before the earlier exchange registration, making the driver's
        // closure visible when the pending write resumes, as a server drain can do.
        host.Cancel();
        fixture.Transport.Writer.Grant(1024 * 1024);
        Exception failure = await Should.ThrowAsync<Exception>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));

        // Assert — the precise pre-latch transport exception is not part of this existing-behavior guard.
        failure.ShouldNotBeOfType<TimeoutException>();
        fixture.Context.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        fixture.Control.ResponseAbortedByClient.ShouldBeFalse();
        fixture.Transport.IsAborted.ShouldBeTrue();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly HttpConnectionListener _listener;
        private readonly IAsyncEnumerator<IHttpContext> _receive;

        private Fixture(StallingConnection transport, HttpConnectionListener listener, IHttpConnectionContext connection,
            IAsyncEnumerator<IHttpContext> receive, IHttpExchangeControl control)
        {
            Transport = transport;
            _listener = listener;
            Connection = connection;
            _receive = receive;
            Control = control;
        }

        public StallingConnection Transport { get; }
        public IHttpConnectionContext Connection { get; }
        public IHttpContext Context => _receive.Current;
        public IHttpExchangeControl Control { get; }

        public static async Task<Fixture> StartAsync(HttpMinDataRate? rate, CancellationToken receiveToken = default)
        {
            TestConnection inner = new(Encoding.ASCII.GetBytes("GET /stream HTTP/1.1\r\nHost: api.test\r\n\r\n"), completeInput: false);
            StallingConnection transport = new(inner);
            CaptureControlInterceptor capture = new();
            HttpConnectionListenerOptions options = new();
            options.Interceptors.Add(new HttpResponseStreamingInterceptor());
            options.Interceptors.Add(capture);
            options.UseHttp1(new TestConnectionListener(transport), http1 => http1.Limits.MinResponseDataRate = rate);
            HttpConnectionListener listener = new(options);
            IHttpConnectionContext connection = await (await listener.AcceptAsync()).OpenAsync();
            IAsyncEnumerator<IHttpContext> receive = connection.ReceiveAsync(receiveToken).GetAsyncEnumerator();
            (await receive.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
            return new Fixture(transport, listener, connection, receive, capture.Control.ShouldNotBeNull());
        }

        public Task WriteAsync(bool streaming, CancellationToken cancellationToken = default)
        {
            if (streaming)
            {
                return Context.Response.Streaming.WriteAsync(new byte[1024], cancellationToken).AsTask();
            }

            Context.Response.Body = new MemoryStream(new byte[1024]);
            return Connection.SendAsync(Context, cancellationToken).AsTask();
        }

        public async ValueTask DisposeAsync()
        {
            await _receive.DisposeAsync();
            await _listener.DisposeAsync();
        }
    }

    private sealed class CaptureControlInterceptor : HttpExchangeInterceptor
    {
        public IHttpExchangeControl? Control { get; private set; }
        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Response;
        public override void BeforeResponse(HttpExchangeInterceptorResponseContext context) => Control = context.Control;
    }
}
