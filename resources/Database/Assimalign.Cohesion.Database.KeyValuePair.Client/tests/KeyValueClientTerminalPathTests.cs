using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.KeyValuePair.Protocol;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client.Tests;

[Collection(nameof(KeyValueClientEventSourceCollection))]
public sealed class KeyValueClientTerminalPathTests
{
    [Theory(DisplayName = "Cohesion Test [KeyValuePair.Client] - TCP handshake terminal paths retain their code for 200 attempts")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectAsync_AuthenticationOrCapacityRefusal_ShouldPreserveCode(bool capacity)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var harness = await KeyValueClientTestHarness.StartAsync(configureServer: options =>
        {
            options.ShutdownDrainTimeout = TimeSpan.FromSeconds(2);
            options.MaxSessions = capacity ? 1 : 100;
            if (!capacity) { options.Authenticator = new TerminalRejectingAuthenticator(); }
        }, tcp: true);
        await using var holder = capacity ? await harness.Client.ConnectAsync(timeout.Token) : null;
        await using var client = KeyValueClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "kv", EndPoint = harness.Listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = new TcpConnectionFactory(),
        });

        // Act / Assert
        for (int attempt = 0; attempt < 200; attempt++)
        {
            var failure = await Should.ThrowAsync<KeyValueClientException>(async () => await client.ConnectAsync(timeout.Token));
            failure.Code.ShouldBe(capacity ? ProtocolErrorCode.Unavailable : ProtocolErrorCode.AuthenticationFailed);
            failure.Kind.ShouldBe(capacity ? KeyValueClientErrorKind.Unavailable : KeyValueClientErrorKind.AuthenticationFailure);
        }
    }

    [Fact(DisplayName = "Cohesion Test [KeyValuePair.Client] - TCP unsupported versions retain their terminal code for 200 attempts")]
    public async Task HandshakeAsync_UnsupportedVersion_ShouldPreserveCode()
    {
        // Arrange: production clients always send Current; use the public protocol for a bad major.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var harness = await KeyValueClientTestHarness.StartAsync(tcp: true);
        var factory = new TcpConnectionFactory();

        // Act / Assert
        for (int attempt = 0; attempt < 200; attempt++)
        {
            await using var peer = await factory.ConnectAsync(harness.Listener.EndPoint, timeout.Token);
            await using var channel = new ProtocolChannel(peer.AsStream(), KeyValueProtocol.Family, leaveOpen: true);
            await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.Startup,
                new ProtocolStartupMessage(new ProtocolVersion(99, 0), "kv", "tester").Encode()), timeout.Token);
            var frame = await channel.Reader.ReadFrameAsync(timeout.Token);
            frame.ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Error);
            ProtocolErrorMessage.Decode(frame.Value.Payload.Span).Code.ShouldBe(ProtocolErrorCode.UnsupportedVersion);
        }
    }

    [Theory(DisplayName = "Cohesion Test [KeyValuePair.Client] - TCP idle eviction and graceful shutdown deliver 200 terminal notices")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadyAsync_IdleOrSoftStop_ShouldDeliverUnavailable(bool shutdown)
    {
        // Arrange: use the concrete client after the server has completed its terminal close.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var harness = await KeyValueClientTestHarness.StartAsync();
        var factory = new TcpConnectionFactory();

        // Act / Assert
        for (int attempt = 0; attempt < 200; attempt++)
        {
            await using var listener = TcpConnectionListener.Create(options => options.EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            await using var server = KeyValueDatabaseServer.Create(harness.Engine, new()
            {
                Listener = listener,
                IdleTimeout = shutdown ? TimeSpan.FromMinutes(1) : TimeSpan.FromMilliseconds(10),
                ShutdownDrainTimeout = TimeSpan.FromSeconds(2),
            });
            await server.StartAsync(timeout.Token);
            await using var client = KeyValueClient.Create(new()
            {
                Settings = new DatabaseConnectionSettings { Database = "kv", EndPoint = listener.EndPoint, MaxPoolSize = 1 },
                ConnectionFactory = factory,
            });
            await using var connection = await client.ConnectAsync(timeout.Token);
            if (shutdown) { await server.StopAsync(timeout.Token); }
            else
            {
                while (server.Sessions.Count != 0) { await Task.Delay(1, timeout.Token); }
            }
            var error = await Should.ThrowAsync<KeyValueClientException>(async () => await connection.GetAsync("absent"u8.ToArray(), timeout.Token));
            error.Code.ShouldBe(ProtocolErrorCode.Unavailable, error.Message);
            error.Kind.ShouldBe(KeyValueClientErrorKind.Unavailable);
            error.Message.ShouldContain(shutdown ? "shutting down" : "idle timeout", Case.Sensitive);
        }
    }
}
