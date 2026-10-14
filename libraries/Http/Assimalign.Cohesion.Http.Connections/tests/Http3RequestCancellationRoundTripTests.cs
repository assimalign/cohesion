using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// The HTTP/3 request cancellation of <see cref="Http3RequestCancellationTests"/> over real QUIC (#1329,
/// RFC 9114 §4.1.1, RFC 9000 §19.4–19.5): a raw QUIC client opens a request whose body is still coming,
/// then aborts one direction of the stream — <c>RESET_STREAM</c> on its sending side or
/// <c>STOP_SENDING</c> on its receiving side — and the server's exchange observes
/// <see cref="IHttpContext.RequestCancelled"/> while the handler neither reads nor answers.
/// </summary>
/// <remarks>
/// System.Net.Quic is Windows/Linux/macOS only, so the class is platform-annotated and every test returns
/// early when <see cref="QuicListener.IsSupported"/> is <see langword="false"/>, matching
/// <see cref="Http3RoundTripTests"/>.
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class Http3RequestCancellationRoundTripTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _cancellationTimeout = TimeSpan.FromSeconds(10);

    // RFC 9114 §8.1 — the code a client uses to cancel a request, and to close a connection cleanly.
    private const long requestCancelled = 0x10c;
    private const long noError = 0x100;

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Cancellation: A client cancelling a request in flight over real QUIC should fire RequestCancelled")]
    [InlineData(QuicAbortDirection.Write)]
    [InlineData(QuicAbortDirection.Read)]
    public async Task RequestCancelled_OnClientCancellationOverRealQuic_ShouldFire(QuicAbortDirection direction)
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — the handler holds the exchange in flight, neither reading nor answering, until the
        // exchange is cancelled or the test gives up.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        TaskCompletionSource dispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using Http3LoopbackServer server = await Http3LoopbackServer.StartAsync(async exchange =>
        {
            using CancellationTokenRegistration registration = exchange.RequestCancelled.Register(() => cancelled.TrySetResult());
            dispatched.TrySetResult();
            await Task.WhenAny(cancelled.Task, Task.Delay(_cancellationTimeout, cancellationToken));
        }, cancellationToken);

        await using QuicConnection connection = await QuicConnection.ConnectAsync(CreateClientOptions(server.BaseUri.Port), cancellationToken);
        await using QuicStream stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken);

        await stream.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "POST"),
            (":scheme", "https"),
            (":path", "/upload"),
            (":authority", "localhost")), cancellationToken);
        await dispatched.Task.WaitAsync(cancellationToken);

        // Act — RESET_STREAM (the write direction) or STOP_SENDING (the read direction).
        stream.Abort(direction, requestCancelled);

        // Assert
        try
        {
            await cancelled.Task.WaitAsync(_cancellationTimeout);
        }
        catch (TimeoutException)
        {
            throw new ShouldAssertException($"RequestCancelled did not fire after the client aborted the {direction} direction of the request stream.");
        }
    }

    private static QuicClientConnectionOptions CreateClientOptions(int port) => new()
    {
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
        DefaultStreamErrorCode = requestCancelled,
        DefaultCloseErrorCode = noError,
        // The server opens its control stream (and QPACK streams when enabled) toward the client.
        MaxInboundUnidirectionalStreams = 3,
        ClientAuthenticationOptions = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http3 },
            TargetHost = "localhost",
            // The loopback certificate is a throwaway self-signed test certificate.
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
        },
    };
}
