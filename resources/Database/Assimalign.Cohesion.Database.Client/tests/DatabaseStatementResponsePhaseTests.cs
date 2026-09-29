using System;
using System.IO;
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

public sealed class DatabaseStatementResponsePhaseTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Client] - Health: a statement error after partial results cannot certify a reusable response")]
    [InlineData(ProtocolErrorCode.ParseFailure)]
    [InlineData(ProtocolErrorCode.ExecutionFailure)]
    public async Task ExecuteAsync_ErrorAfterResultFrames_ShouldPreserveCodeAndDiscardRental(ProtocolErrorCode code)
    {
        // Arrange: the scripted peer leaves a completion frame after a partial-response error.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var listener = new InMemoryConnectionListener();
        await using var client = DatabaseClient.Create(new DatabaseClientOptions
        {
            Settings = new DatabaseConnectionSettings
            {
                Database = "sql", EndPoint = listener.EndPoint, MaxPoolSize = 1,
            },
            ConnectionFactory = listener.CreateFactory(),
            Family = SqlProtocol.Family,
        });
        Task server = ServeAsync(listener, code, timeout.Token);
        try
        {
            var original = await client.RentAsync(timeout.Token);
            var failure = await Should.ThrowAsync<DatabaseClientException>(async () =>
                await original.ExecuteAsync("SELECT partial", cancellationToken: timeout.Token));

            // Act: the real code survives, but the pool must discard this unfinished response.
            failure.Code.ShouldBe(code);
            failure.Message.ShouldBe("Failure after result frames.");
            await original.DisposeAsync();
            await using var next = await client.RentAsync(timeout.Token);

            // Assert: the replacement executes a clean response, never the leftover completion.
            next.ShouldNotBeSameAs(original);
            var result = await next.ExecuteAsync("SELECT clean", cancellationToken: timeout.Token);
            result.AffectedCount.ShouldBe(0);
        }
        finally
        {
            await client.DisposeAsync();
            timeout.Cancel();
            await server;
        }
    }

    private static async Task ServeAsync(InMemoryConnectionListener listener, ProtocolErrorCode code,
        CancellationToken cancellationToken)
    {
        try
        {
            for (int accepted = 0; accepted < 2; accepted++)
            {
                await using var transport = await listener.AcceptAsync(cancellationToken);
                await using var channel = new ProtocolChannel(transport.AsStream(), SqlProtocol.Family);
                try
                {
                    (await ReadAsync(channel.Reader, cancellationToken)).Type.ShouldBe(ProtocolMessageType.Startup);
                    await WriteAsync(channel.Writer, ProtocolMessageType.Authenticate, ReadOnlyMemory<byte>.Empty, cancellationToken);
                    (await ReadAsync(channel.Reader, cancellationToken)).Type.ShouldBe(ProtocolMessageType.AuthenticateResponse);
                    await WriteAsync(channel.Writer, ProtocolMessageType.Ready, ReadOnlyMemory<byte>.Empty, cancellationToken);
                    (await ReadAsync(channel.Reader, cancellationToken)).Type.ShouldBe((ProtocolMessageType)SqlProtocolMessageType.Execute);

                    if (accepted == 0)
                    {
                        await channel.Writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)SqlProtocolMessageType.ResultHeader,
                            new ProtocolResultHeaderMessage([]).Encode()), cancellationToken);
                        await channel.Writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)SqlProtocolMessageType.ResultRow,
                            ReadOnlyMemory<byte>.Empty), cancellationToken);
                        await channel.Writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Error,
                            new ProtocolErrorMessage(code, "Failure after result frames.").Encode()), cancellationToken);
                    }
                    await WriteAsync(channel.Writer, (ProtocolMessageType)SqlProtocolMessageType.ResultComplete,
                        new ProtocolResultCompleteMessage(accepted == 0 ? 999 : 0).Encode(), cancellationToken);
                    while (await channel.Reader.ReadFrameAsync(cancellationToken) is { } frame)
                    {
                        if (frame.Type == ProtocolMessageType.Terminate)
                        {
                            break;
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or ConnectionAbortedException or ConnectionResetException)
                {
                    // The test deliberately closes a peer with an unread completion frame.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static async ValueTask<ProtocolFrame> ReadAsync(IProtocolFrameReader reader, CancellationToken cancellationToken)
        => await reader.ReadFrameAsync(cancellationToken) ?? throw new ProtocolException("The scripted SQL peer closed unexpectedly.");

    private static async ValueTask WriteAsync(IProtocolFrameWriter writer, ProtocolMessageType type,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await writer.WriteFrameAsync(new ProtocolFrame(type, payload), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }
}
