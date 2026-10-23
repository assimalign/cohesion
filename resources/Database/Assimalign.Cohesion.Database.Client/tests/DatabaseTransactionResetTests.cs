using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Protocol;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Client.Tests;

public sealed class DatabaseTransactionResetTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Client] - Pool reset discards uncertified transaction sessions (#1540)")]
    [InlineData("invalid-status")]
    [InlineData("wrong-frame")]
    [InlineData("primary-error")]
    [InlineData("rollback-error")]
    [InlineData("rollback-still-active")]
    [InlineData("legacy")]
    public async Task DisposeAsync_UncertifiedReset_ShouldRequireNewHandshake(string scenario)
    {
        // A 30-second hang guard bounds a broken scripted-peer protocol; duration decides nothing.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var listener = new InMemoryConnectionListener();
        await using var client = DatabaseClient.Create(new()
        {
            Family = SqlProtocol.Family,
            Settings = new() { Database = "sql", EndPoint = listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = listener.CreateFactory(),
        });
        Task peer = ServeAsync(listener, scenario, timeout.Token);
        try
        {
            await using var first = await client.RentAsync(timeout.Token);
            if (scenario == "primary-error")
            {
                var expected = new InvalidOperationException("Original complete statement failure.");
                (await Should.ThrowAsync<InvalidOperationException>(async () =>
                    await first.ExecuteAsync(new CompleteFailureExchange(expected), timeout.Token))).ShouldBeSameAs(expected);
                first.IsOpen.ShouldBeFalse();
            }
            else if (scenario is "invalid-status" or "wrong-frame")
            {
                (await Should.ThrowAsync<DatabaseClientException>(async () =>
                    await first.ExecuteAsync("BEGIN", cancellationToken: timeout.Token))).Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
                first.IsOpen.ShouldBeFalse();
            }
            else { await first.ExecuteAsync("BEGIN", cancellationToken: timeout.Token); }
            await first.DisposeAsync();
            await using var next = await client.RentAsync(timeout.Token);
            (await next.ExecuteAsync("SELECT clean", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(0);
        }
        finally
        {
            await client.DisposeAsync();
            timeout.Cancel();
            await peer;
        }
    }

    private static async Task ServeAsync(InMemoryConnectionListener listener, string scenario, CancellationToken token)
    {
        try
        {
            for (int accepted = 0; accepted < 2; accepted++)
            {
                await using var transport = await listener.AcceptAsync(token);
                await using var channel = new ProtocolChannel(transport.AsStream(), SqlProtocol.Family);
                try
                {
                    var startup = ProtocolStartupMessage.Decode((await ReadAsync(channel, token)).Payload.Span);
                    await WriteAsync(channel, ProtocolMessageType.Authenticate, [], token);
                    (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.AuthenticateResponse);
                    bool legacy = scenario == "legacy" && accepted == 0;
                    byte[] ready = legacy ? [] : channel.Negotiate(startup.Version, startup.Capabilities).Encode();
                    await WriteAsync(channel, ProtocolMessageType.Ready, ready, token);
                    (await ReadAsync(channel, token)).Type.ShouldBe((ProtocolMessageType)5);
                    await WriteAsync(channel, (ProtocolMessageType)8, new ProtocolResultCompleteMessage(0).Encode(), token);
                    if (!legacy)
                    {
                        var type = scenario == "wrong-frame" && accepted == 0 ? ProtocolMessageType.Pong : ProtocolMessageType.Ready;
                        byte[] status = accepted == 1 ? [(byte)'I'] : scenario is "invalid-status" or "primary-error" ? [0] : [(byte)'T'];
                        await WriteAsync(channel, type, status, token);
                    }
                    if (accepted == 0 && scenario.StartsWith("rollback-", StringComparison.Ordinal))
                    {
                        var rollback = await ReadAsync(channel, token);
                        rollback.Type.ShouldBe((ProtocolMessageType)5);
                        ProtocolExecuteMessage.Decode(rollback.Payload.Span).Statement.ShouldBe("ROLLBACK");
                        if (scenario == "rollback-error")
                        { await WriteAsync(channel, ProtocolMessageType.Error, new ProtocolErrorMessage(ProtocolErrorCode.ExecutionFailure, "Reset refused.").Encode(), token); }
                        else
                        {
                            await WriteAsync(channel, (ProtocolMessageType)8, new ProtocolResultCompleteMessage(0).Encode(), token);
                            await WriteAsync(channel, ProtocolMessageType.Ready, [(byte)'T'], token);
                        }
                    }
                    while (await channel.Reader.ReadFrameAsync(token) is { } frame)
                    { if (frame.Type == ProtocolMessageType.Terminate) { break; } }
                }
                catch (IOException) { /* The client discards the deliberately malformed peer. */ }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static async ValueTask<ProtocolFrame> ReadAsync(ProtocolChannel channel, CancellationToken token)
        => await channel.Reader.ReadFrameAsync(token) ?? throw new ProtocolException("Scripted peer closed early.");

    private static async ValueTask WriteAsync(ProtocolChannel channel, ProtocolMessageType type, byte[] payload, CancellationToken token)
    {
        await channel.Writer.WriteFrameAsync(new(type, payload), token);
        await channel.Writer.FlushAsync(token);
    }

    private sealed class CompleteFailureExchange(Exception failure) : DatabaseProtocolExchange<bool>(SqlProtocol.Family, transactionStatus: true)
    {
        protected override async ValueTask<bool> ExecuteCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer, CancellationToken token)
        {
            await writer.WriteFrameAsync(new((ProtocolMessageType)5, ProtocolExecuteMessage.Create("BEGIN").Encode()), token);
            await writer.FlushAsync(token);
            (await reader.ReadFrameAsync(token))!.Value.Type.ShouldBe((ProtocolMessageType)8);
            MarkResponseComplete();
            throw failure;
        }
    }
}
