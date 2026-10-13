using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>Peer aborts during the HTTP/3 extended CONNECT head or DATA must publish the response latch.</summary>
public class Http3ExtendedConnectClientFaultTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 ExtendedConnect Client Fault: Peer reset or STOP_SENDING during head or DATA should latch before the tokenless write fails")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ResponseAbortedByClient_OnPeerAbortDuringTunnelWrite_ShouldLatchAndCancel(bool head, bool stopSending)
    {
        // Arrange — the response rate deliberately does not bound a tunnel, so only the peer's abort
        // can release this tokenless write while the transport withholds credit.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        StallingMultiplexedConnection? server = null;
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            http3 => http3.Limits.MinResponseDataRate = null,
            ConfigureInterceptors,
            inner => server = new StallingMultiplexedConnection(inner));
        await using MultiplexedConnectionStream request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "CONNECT"),
            (":protocol", "websocket"),
            (":scheme", "https"),
            (":path", "/chat"),
            (":authority", "a")), timeout.Token);
        await using IHttpContext context = await peer.NextContextAsync();
        IHttpExchangeControl control = context.Features.Get<CapturedControlFeature>().ShouldNotBeNull().Control;
        Stream? tunnel = head ? null : await context.ExtendedConnect!.AcceptAsync(timeout.Token);
        StallingPipeWriter writer = server.ShouldNotBeNull().RequestStreams[0].Writer;
        writer.Stall();
        Task pending = head
            ? context.ExtendedConnect!.AcceptAsync(CancellationToken.None).AsTask()
            : tunnel!.WriteAsync(new byte[1024], CancellationToken.None).AsTask();
        while (!writer.IsFlushWaiting)
        {
            await Task.Delay(1, timeout.Token);
        }
        control.ResponseAbortedByClient.ShouldBeFalse();

        // Act
        if (stopSending)
        {
            request!.DirectionalAbort!.AbortRead((long)Http3ErrorCode.RequestCancelled);
        }
        else
        {
            request!.DirectionalAbort!.AbortWrite((long)Http3ErrorCode.RequestCancelled);
        }

        try
        {
            if (head)
            {
                OperationCanceledException failure = await Should.ThrowAsync<OperationCanceledException>(
                    () => pending.WaitAsync(timeout.Token));
                failure.CancellationToken.ShouldBe(context.RequestCancelled);
            }
            else
            {
                // The tunnel's existing Stream contract wraps transport cancellation as IOException.
                IOException failure = await Should.ThrowAsync<IOException>(() => pending.WaitAsync(timeout.Token));
                failure.InnerException.ShouldBeOfType<OperationCanceledException>()
                    .CancellationToken.ShouldBe(context.RequestCancelled);
            }

            // Assert — the application sees both facts at the point it catches the failed operation.
            control.ResponseAbortedByClient.ShouldBeTrue();
            context.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
            control.ClientFaultStatusCode.ShouldBeNull();
        }
        finally
        {
            // Also release the broken implementation during the pre-fix run, without leaving its write
            // alive after the test disposes the exchange.
            writer.Grant(1024 * 1024);
            try
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
            }
            if (tunnel is not null)
            {
                await tunnel.DisposeAsync();
            }
        }
    }

    private static void ConfigureInterceptors(HttpConnectionListenerOptions options)
    {
        options.Interceptors.Add(HttpExtendedConnect.CreateInterceptor());
        options.Interceptors.Add(new CaptureControlInterceptor());
    }

    private sealed class CaptureControlInterceptor : HttpExchangeInterceptor
    {
        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Response;

        public override void BeforeResponse(HttpExchangeInterceptorResponseContext context)
            => context.Features.Set(new CapturedControlFeature(context.Control.ShouldNotBeNull()));
    }

    private sealed class CapturedControlFeature : IHttpFeature
    {
        public CapturedControlFeature(IHttpExchangeControl control)
        {
            Control = control;
        }

        public string Name => nameof(CapturedControlFeature);

        public IHttpExchangeControl Control { get; }
    }
}
