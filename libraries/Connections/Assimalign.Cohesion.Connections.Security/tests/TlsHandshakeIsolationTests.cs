using System;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Connections.Security.Tests;

/// <summary>
/// Covers how a TLS-layered listener (<c>listener.UseTls(options)</c>) contains a handshake that fails or
/// stalls (#1304): the handshake runs per connection, a failure or timeout closes only that connection while
/// another client is secured on the same listener, and <see cref="TlsServerOptions.MaxConcurrentHandshakes"/>
/// bounds the connections held at once.
/// </summary>
public class TlsHandshakeIsolationTests : IClassFixture<TestCertificateFixture>
{
    // A hang guard, never a budget: every wait completes as soon as the listener does its part.
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    private readonly TestCertificateFixture _fixture;

    public TlsHandshakeIsolationTests(TestCertificateFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Security] - UseTls: Bytes that are not a TLS handshake should close only that connection")]
    public async Task UseTls_AfterNonTlsBytes_ShouldCloseThatConnectionAndSecureTheNext()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (Connection garbageClient, Connection garbageServer) = InMemoryConnectionPair.Create();
        (Connection client, Connection server) = InMemoryConnectionPair.Create();
        BlockingConnectionListener transport = new();
        transport.Enqueue(garbageServer);
        transport.Enqueue(server);
        await using IConnectionListener listener = transport.UseTls(CreateServerOptions());
        await garbageClient.Output.WriteAsync("HELLO-1304"u8.ToArray(), timeout.Token);

        // Act
        Task<IConnection> accept = listener.AcceptAsync(timeout.Token).AsTask();
        IConnection secured = await AcceptWithClientHandshakeAsync(accept, client, clientCertificate: null, timeout.Token);

        // Assert
        secured.Id.ShouldBe(server.Id);
        await WhenClosedAsync(garbageServer, timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Security] - UseTls: A client the certificate policy refuses should close only that connection")]
    public async Task UseTls_WhenTheRequiredClientCertificateIsMissing_ShouldCloseThatConnectionAndSecureTheNext()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        string expected = _fixture.ClientCertificate.Thumbprint;
        TlsServerOptions options = CreateServerOptions().RequireClientCertificate((presented, _, _) => presented.Thumbprint == expected);
        (Connection refusedClient, Connection refusedServer) = InMemoryConnectionPair.Create();
        (Connection client, Connection server) = InMemoryConnectionPair.Create();
        BlockingConnectionListener transport = new();
        transport.Enqueue(refusedServer);
        transport.Enqueue(server);
        await using IConnectionListener listener = transport.UseTls(options);
        Task refusedHandshake = ObserveAsync(refusedClient.UpgradeToTlsAsync(CreateClientOptions(clientCertificate: null), timeout.Token).AsTask());

        // Act
        Task<IConnection> accept = listener.AcceptAsync(timeout.Token).AsTask();
        IConnection secured = await AcceptWithClientHandshakeAsync(accept, client, _fixture.ClientCertificate, timeout.Token);

        // Assert
        secured.Id.ShouldBe(server.Id);
        secured.ShouldBeAssignableTo<ITlsConnectionInfo>()!.RemoteCertificate!.Thumbprint.ShouldBe(expected);
        await WhenClosedAsync(refusedServer, timeout.Token);
        await refusedHandshake;
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Security] - UseTls: A silent client should not delay another client's handshake")]
    public async Task UseTls_WhileAClientStaysSilent_ShouldSecureAnotherClient()
    {
        // Arrange — left alone, the silent client would hold its handshake for minutes.
        using CancellationTokenSource timeout = new(_testTimeout);
        TlsServerOptions options = CreateServerOptions();
        options.HandshakeTimeout = TimeSpan.FromMinutes(5);
        (Connection _, Connection silentServer) = InMemoryConnectionPair.Create();
        (Connection client, Connection server) = InMemoryConnectionPair.Create();
        BlockingConnectionListener transport = new();
        transport.Enqueue(silentServer);
        transport.Enqueue(server);
        IConnectionListener listener = transport.UseTls(options);

        // Act
        Task<IConnection> accept = listener.AcceptAsync(timeout.Token).AsTask();
        IConnection secured = await AcceptWithClientHandshakeAsync(accept, client, clientCertificate: null, timeout.Token);
        bool silentOpenWhileServing = !silentServer.ConnectionClosed.IsCancellationRequested;
        await listener.DisposeAsync();

        // Assert — disposal cancels the handshake still waiting and closes its connection.
        secured.Id.ShouldBe(server.Id);
        silentOpenWhileServing.ShouldBeTrue();
        silentServer.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Security] - UseTls: A handshake that times out should close its connection and keep the listener accepting")]
    public async Task UseTls_WhenASilentClientExceedsTheHandshakeTimeout_ShouldCloseItAndKeepAccepting()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TlsServerOptions options = CreateServerOptions();
        options.HandshakeTimeout = TimeSpan.FromSeconds(2);
        (Connection _, Connection silentServer) = InMemoryConnectionPair.Create();
        (Connection client, Connection server) = InMemoryConnectionPair.Create();
        BlockingConnectionListener transport = new();
        transport.Enqueue(silentServer);
        await using IConnectionListener listener = transport.UseTls(options);
        Task<IConnection> accept = listener.AcceptAsync(timeout.Token).AsTask();

        // Act — the silent client's handshake times out first; a client that arrives afterwards is served.
        await WhenClosedAsync(silentServer, timeout.Token);
        transport.Enqueue(server);
        IConnection secured = await AcceptWithClientHandshakeAsync(accept, client, clientCertificate: null, timeout.Token);

        // Assert
        secured.Id.ShouldBe(server.Id);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Security] - UseTls: Should stop accepting from the transport at MaxConcurrentHandshakes")]
    public async Task UseTls_AtMaxConcurrentHandshakes_ShouldStopAcceptingFromTheTransport()
    {
        // Arrange — one slot, taken by a client that never speaks.
        using CancellationTokenSource timeout = new(_testTimeout);
        TlsServerOptions options = CreateServerOptions();
        options.HandshakeTimeout = TimeSpan.FromMinutes(5);
        options.MaxConcurrentHandshakes = 1;
        (Connection silentClient, Connection silentServer) = InMemoryConnectionPair.Create();
        (Connection client, Connection server) = InMemoryConnectionPair.Create();
        BlockingConnectionListener transport = new();
        transport.Enqueue(silentServer);
        transport.Enqueue(server);
        await using IConnectionListener listener = transport.UseTls(options);
        Task<IConnection> accept = listener.AcceptAsync(timeout.Token).AsTask();
        await WaitUntilAsync(() => transport.AcceptCalls >= 1, timeout.Token);

        // Act — the pause gives a listener that ignored the limit time to take the second connection; it
        // cannot make a correct listener fail. The silent client then hangs up, which ends its handshake
        // and frees the slot.
        await Task.Delay(TimeSpan.FromMilliseconds(200), timeout.Token);
        int acceptsWhileFull = transport.AcceptCalls;
        await silentClient.DisposeAsync();
        IConnection secured = await AcceptWithClientHandshakeAsync(accept, client, clientCertificate: null, timeout.Token);

        // Assert
        acceptsWhileFull.ShouldBe(1);
        secured.Id.ShouldBe(server.Id);
        silentServer.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
    }

    private TlsServerOptions CreateServerOptions()
    {
        return new TlsServerOptions
        {
            AuthenticationOptions =
            {
                ServerCertificate = _fixture.Certificate
            }
        };
    }

    private static TlsClientOptions CreateClientOptions(X509Certificate2? clientCertificate)
    {
        TlsClientOptions options = new()
        {
            AuthenticationOptions =
            {
                TargetHost = "localhost",
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            }
        };

        if (clientCertificate is not null)
        {
            TlsConnectionInfoTests.UseClientCertificate(options, clientCertificate);
        }

        return options;
    }

    /// <summary>
    /// Runs a well-behaved client's handshake on <paramref name="client"/> while the listener accepts, and
    /// returns the secured connection the listener produced.
    /// </summary>
    private static async Task<IConnection> AcceptWithClientHandshakeAsync(
        Task<IConnection> accept,
        Connection client,
        X509Certificate2? clientCertificate,
        CancellationToken cancellationToken)
    {
        Task<IConnection> clientHandshake = client.UpgradeToTlsAsync(CreateClientOptions(clientCertificate), cancellationToken).AsTask();
        IConnection accepted = await accept;
        await clientHandshake;

        return accepted;
    }

    private static async Task WhenClosedAsync(Connection connection, CancellationToken cancellationToken)
    {
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = connection.ConnectionClosed.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            closed);

        await closed.Task.WaitAsync(cancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    private static async Task ObserveAsync(Task<IConnection> handshake)
    {
        try
        {
            IConnection connection = await handshake;
            await connection.DisposeAsync();
        }
        catch (Exception exception) when (exception is AuthenticationException or System.IO.IOException or OperationCanceledException)
        {
            // Under TLS 1.3 the client may finish its side before the server refuses it, or learn of the
            // refusal from the alert; only the server's handling is under test.
        }
    }
}
