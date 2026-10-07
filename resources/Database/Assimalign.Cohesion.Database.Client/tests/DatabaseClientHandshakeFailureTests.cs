using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Client;

namespace Assimalign.Cohesion.Database.Client.Tests;

/// <summary>
/// The failures owner decision 39 leaves after the dial: a transport the peer breaks during the
/// handshake reaches the caller of <see cref="DatabaseClient.RentAsync"/> as a
/// <see cref="DatabaseClientException"/> with <see cref="ProtocolErrorCode.Internal"/> and the
/// transport's exception inside, never raw; and a peer that sends the client-local
/// <see cref="ProtocolErrorCode.ConnectionFailure"/> code, or a malformed error frame, breaks
/// the protocol.
/// </summary>
public sealed class DatabaseClientHandshakeFailureTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Client] - Handshake: a peer reset after the startup frame is wrapped with the socket error and frees the slot")]
    public async Task RentAsync_PeerResetsDuringHandshake_ShouldWrapSocketException()
    {
        // Arrange: the peer accepts each connection, reads the startup frame and resets it. The TCP
        // transport completes the connection's input with the raw SocketException in that case,
        // which the in-memory transport's abort reproduces.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var listener = new InMemoryConnectionListener();
        await using var client = CreateClient(listener);
        Task server = Task.Run(async () =>
        {
            for (int accepted = 0; accepted < 2; accepted++)
            {
                await using Connection connection = await listener.AcceptAsync(timeout.Token);
                ReadResult startup = await connection.Input.ReadAsync(timeout.Token);
                connection.Input.AdvanceTo(startup.Buffer.End);
                connection.Abort(new SocketException((int)SocketError.ConnectionReset));
            }
        });

        // Act
        var first = await Should.ThrowAsync<DatabaseClientException>(async () => await client.RentAsync(timeout.Token));

        // The pool holds one connection, so the peer accepts a second one only if the failed
        // handshake released its slot.
        var second = await Should.ThrowAsync<DatabaseClientException>(async () => await client.RentAsync(timeout.Token));
        await server;

        // Assert
        first.Code.ShouldBe(ProtocolErrorCode.Internal);
        first.InnerException.ShouldBeOfType<SocketException>().SocketErrorCode.ShouldBe(SocketError.ConnectionReset);
        second.InnerException.ShouldBeOfType<SocketException>();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Handshake: a startup error frame carrying the client-local ConnectionFailure code is a protocol violation")]
    public async Task RentAsync_HandshakeErrorCarriesConnectionFailure_ShouldThrowProtocolViolation()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var listener = new InMemoryConnectionListener();
        await using var client = CreateClient(listener);
        Task server = RejectStartupAsync(listener, new ProtocolErrorMessage(ProtocolErrorCode.ConnectionFailure, "Spoofed dial failure.").Encode(), timeout.Token);

        // Act
        var exception = await Should.ThrowAsync<DatabaseClientException>(async () => await client.RentAsync(timeout.Token));
        await server;

        // Assert
        exception.Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
        exception.Message.ShouldContain("Spoofed dial failure.", Case.Sensitive);
        exception.InnerException.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Handshake: a malformed startup error frame is a protocol violation, not a raw ProtocolException")]
    public async Task RentAsync_MalformedHandshakeError_ShouldThrowProtocolViolation()
    {
        // Arrange: one byte cannot hold the error frame's u16 code.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var listener = new InMemoryConnectionListener();
        await using var client = CreateClient(listener);
        Task server = RejectStartupAsync(listener, new byte[] { 0x01 }, timeout.Token);

        // Act
        var exception = await Should.ThrowAsync<DatabaseClientException>(async () => await client.RentAsync(timeout.Token));
        await server;

        // Assert
        exception.Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
        exception.InnerException.ShouldBeOfType<ProtocolException>();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Exchange: an error frame carrying the client-local ConnectionFailure code is a protocol violation and discards the rental")]
    public async Task ExecuteAsync_ErrorCarriesConnectionFailure_ShouldThrowProtocolViolation()
    {
        // Arrange: a peer that completes the handshake, then answers the statement with the
        // client-local code.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var listener = new InMemoryConnectionListener();
        await using var client = CreateClient(listener);
        Task server = AnswerStatementAsync(listener, timeout.Token);
        try
        {
            await using DatabaseConnection connection = await client.RentAsync(timeout.Token);

            // Act
            var exception = await Should.ThrowAsync<DatabaseClientException>(async () =>
                await connection.ExecuteAsync("SELECT 1", cancellationToken: timeout.Token));

            // Assert
            exception.Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
            exception.Message.ShouldContain("Spoofed dial failure.", Case.Sensitive);
            exception.InnerException.ShouldBeNull();
            connection.IsOpen.ShouldBeFalse();
        }
        finally
        {
            await client.DisposeAsync();
            await server;
        }
    }

    private static DatabaseClient CreateClient(InMemoryConnectionListener listener)
        => DatabaseClient.Create(new DatabaseClientOptions
        {
            Settings = new DatabaseConnectionSettings
            {
                Database = ClientTestHarness.DatabaseName,
                Principal = "tester",
                EndPoint = listener.EndPoint,
                MaxPoolSize = 1,
            },
            ConnectionFactory = listener.CreateFactory(),
            Family = SqlProtocol.Family,
        });

    private static async Task RejectStartupAsync(InMemoryConnectionListener listener, byte[] errorPayload, CancellationToken cancellationToken)
    {
        await using Connection transport = await listener.AcceptAsync(cancellationToken);
        await using var channel = new ProtocolChannel(transport.AsStream(), SqlProtocol.Family);
        (await ReadAsync(channel.Reader, cancellationToken)).Type.ShouldBe(ProtocolMessageType.Startup);
        await WriteAsync(channel.Writer, ProtocolMessageType.Error, errorPayload, cancellationToken);
        await DrainAsync(channel.Reader, cancellationToken);
    }

    private static async Task AnswerStatementAsync(InMemoryConnectionListener listener, CancellationToken cancellationToken)
    {
        await using Connection transport = await listener.AcceptAsync(cancellationToken);
        await using var channel = new ProtocolChannel(transport.AsStream(), SqlProtocol.Family);
        (await ReadAsync(channel.Reader, cancellationToken)).Type.ShouldBe(ProtocolMessageType.Startup);
        await WriteAsync(channel.Writer, ProtocolMessageType.Authenticate, ReadOnlyMemory<byte>.Empty, cancellationToken);
        (await ReadAsync(channel.Reader, cancellationToken)).Type.ShouldBe(ProtocolMessageType.AuthenticateResponse);
        await WriteAsync(channel.Writer, ProtocolMessageType.Ready, ReadOnlyMemory<byte>.Empty, cancellationToken);
        (await ReadAsync(channel.Reader, cancellationToken)).Type.ShouldBe((ProtocolMessageType)SqlProtocolMessageType.Execute);
        await WriteAsync(channel.Writer, ProtocolMessageType.Error,
            new ProtocolErrorMessage(ProtocolErrorCode.ConnectionFailure, "Spoofed dial failure.").Encode(), cancellationToken);
        await DrainAsync(channel.Reader, cancellationToken);
    }

    // Reads until the client closes its end, so the peer outlives the client's last read.
    private static async Task DrainAsync(ProtocolFrameReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadFrameAsync(cancellationToken) is not null)
            {
            }
        }
        catch (Exception exception) when (exception is IOException or ConnectionAbortedException or ConnectionResetException)
        {
            // The client closed a connection it found broken.
        }
    }

    private static async ValueTask<ProtocolFrame> ReadAsync(ProtocolFrameReader reader, CancellationToken cancellationToken)
        => await reader.ReadFrameAsync(cancellationToken) ?? throw new ProtocolException("The scripted peer's client closed unexpectedly.");

    private static async ValueTask WriteAsync(ProtocolFrameWriter writer, ProtocolMessageType type,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await writer.WriteFrameAsync(new ProtocolFrame(type, payload), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }
}
