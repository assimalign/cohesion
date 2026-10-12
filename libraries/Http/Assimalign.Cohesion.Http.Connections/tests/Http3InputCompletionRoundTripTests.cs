using System;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>Exercises the QUIC driver's received-FIN facet through the HTTP/3 body and response gates.</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class Http3InputCompletionRoundTripTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [QuicTheory(DisplayName = "Cohesion Test [Http.Connections] - HTTP/3 FIN: A head sent with FIN over real QUIC should skip the body-gated response phase")]
    [InlineData("GET", "", false)]
    [InlineData("POST", "", false)]
    [InlineData("POST", "hello", true)]
    public async Task CanHaveBody_OnRealQuicRequestEndedWithItsHeadOrData_ShouldPreserveTheBodyGate(string method, string content, bool expected)
    {
        // Arrange — use the protocol feature defaults and the Web host's client-fault body predicate.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        BodyGatedResponseInterceptor interceptor = new();
        TaskCompletionSource<(bool? CanHaveBody, bool HasSink, bool ResponseHookRan, string Body)> observed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Http3LoopbackServer server = await Http3LoopbackServer.StartAsync(
            async exchange =>
            {
                using MemoryStream body = new();
                await exchange.Request.Body.CopyToAsync(body, cancellationToken);
                observed.TrySetResult((
                    interceptor.CanHaveBody,
                    ((TransportHttpContext)exchange).ResponseBodySink is not null,
                    interceptor.ResponseHookRan,
                    Encoding.ASCII.GetString(body.ToArray())));
                exchange.Response.StatusCode = HttpStatusCode.Ok;
            },
            configure: null,
            configureListener: options =>
            {
                options.Interceptors.Add(HttpRequestLimits.CreateInterceptor());
                options.Interceptors.Add(HttpProtocolUpgrade.CreateInterceptor());
                options.Interceptors.Add(HttpExtendedConnect.CreateInterceptor());
                options.Interceptors.Add(interceptor);
            },
            cancellationToken);
        await using QuicConnection connection = await QuicConnection.ConnectAsync(CreateClientOptions(server.BaseUri.Port), cancellationToken);
        await using QuicStream stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken);

        // Act — the head (and optional DATA) and FIN are one QUIC send, so the driver holds the end when
        // it finishes copying the last received octets into the stream-backed input pipe.
        await stream.WriteAsync(
            HttpProtocolPayloadFactory.CreateHttp3Request(method, "/", "https", "localhost", body: Encoding.ASCII.GetBytes(content)),
            completeWrites: true,
            cancellationToken);
        using MemoryStream response = new();
        await stream.CopyToAsync(response, cancellationToken);
        (bool? canHaveBody, bool hasSink, bool responseHookRan, string body) = await observed.Task.WaitAsync(cancellationToken);

        // Assert — a bodiless GET/POST creates no response sink or response hook; buffered POST data is
        // still exposed and read in full even though the transport has already received its FIN.
        canHaveBody.ShouldBe(expected);
        hasSink.ShouldBe(expected);
        responseHookRan.ShouldBe(expected);
        body.ShouldBe(content);
        response.Length.ShouldBeGreaterThan(0L);
    }

    private static QuicClientConnectionOptions CreateClientOptions(int port) => new()
    {
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
        DefaultStreamErrorCode = 0x10c,
        DefaultCloseErrorCode = 0x100,
        MaxInboundUnidirectionalStreams = 3,
        ClientAuthenticationOptions = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = [SslApplicationProtocol.Http3],
            TargetHost = "localhost",
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
        },
    };

    private sealed class BodyGatedResponseInterceptor : HttpExchangeInterceptor
    {
        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;

        public bool? CanHaveBody { get; private set; }

        public bool ResponseHookRan { get; private set; }

        public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
        {
            CanHaveBody = context.CanHaveBody;
            if (context.CanHaveBody)
            {
                context.AddResponseInterceptor(this);
            }
        }

        public override void BeforeResponse(HttpExchangeInterceptorResponseContext context)
        {
            ResponseHookRan = true;
        }
    }
}
