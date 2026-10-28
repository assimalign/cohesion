using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Documents.Protocol;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client.Tests;

public sealed class DocumentTransactionResetTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Client] - Documents reset validates its envelope and discards every uncertified peer on both transports (#1725)")]
    [InlineData(false, "active")]
    [InlineData(true, "active")]
    [InlineData(false, "failed")]
    [InlineData(true, "failed")]
    [InlineData(false, "legacy")]
    [InlineData(true, "legacy")]
    [InlineData(false, "unnegotiated")]
    [InlineData(true, "unnegotiated")]
    [InlineData(false, "invalid-status")]
    [InlineData(true, "invalid-status")]
    [InlineData(false, "missing-status")]
    [InlineData(true, "missing-status")]
    [InlineData(false, "primary-error")]
    [InlineData(true, "primary-error")]
    [InlineData(false, "rollback-width")]
    [InlineData(true, "rollback-width")]
    [InlineData(false, "rollback-results")]
    [InlineData(true, "rollback-results")]
    [InlineData(false, "rollback-affected")]
    [InlineData(true, "rollback-affected")]
    [InlineData(false, "rollback-error")]
    [InlineData(true, "rollback-error")]
    [InlineData(false, "rollback-active")]
    [InlineData(true, "rollback-active")]
    [InlineData(false, "rollback-failed")]
    [InlineData(true, "rollback-failed")]
    [InlineData(false, "rollback-status")]
    [InlineData(true, "rollback-status")]
    [InlineData(false, "rollback-close")]
    [InlineData(true, "rollback-close")]
    public async Task DisposeAsync_UncertifiedDocumentState_ShouldDiscardOrReset(bool tcp, string scenario)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = timeout.Token;
        await using IDuplexConnectionListener listener = tcp
            ? new TcpConnectionListener(new() { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) })
            : new InMemoryConnectionListener();
        IDuplexConnectionFactory factory = listener is InMemoryConnectionListener memory ? memory.CreateFactory() : new TcpConnectionFactory();
        await listener.BindAsync(token);
        await using var client = DatabaseClient.Create(new()
        {
            Family = DocumentProtocol.Family,
            Settings = new() { Database = "documents", EndPoint = listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = factory,
        });
        bool reuse = scenario is "active" or "failed";
        int accepted = 0;
        int rollbacks = 0;
        Task peer = ServeAsync();
        try
        {
            // Act
            await using var first = await client.RentAsync(token);
            var expected = scenario == "primary-error" ? new InvalidOperationException("Original complete failure.") : null;
            if (expected is not null)
            {
                (await Should.ThrowAsync<InvalidOperationException>(async () =>
                    await first.ExecuteAsync(new Exchange(false, expected), token))).ShouldBeSameAs(expected);
            }
            else if (scenario is "invalid-status" or "missing-status")
            { await Should.ThrowAsync<DatabaseClientException>(async () => await first.ExecuteAsync(new Exchange(false), token)); }
            else { await first.ExecuteAsync(new Exchange(false), token); }
            await first.DisposeAsync();
            await using var next = await client.RentAsync(token);
            // Command uses the default exchange constructor: shared request observation must drain Ready.
            (await next.ExecuteAsync(new Exchange(true), token)).ShouldBe(0);
            await client.DisposeAsync();
            await next.DisposeAsync();
            await peer.WaitAsync(token);

            // Assert
            accepted.ShouldBe(reuse ? 1 : 2);
            rollbacks.ShouldBe(reuse || scenario.StartsWith("rollback-", StringComparison.Ordinal) ? 1 : 0);
        }
        finally
        {
            await client.DisposeAsync();
            await timeout.CancelAsync();
            await peer;
        }

        async Task ServeAsync()
        {
            try
            {
                for (int index = 0; index < (reuse ? 1 : 2); index++)
                {
                    await using var transport = await listener.AcceptAsync(token);
                    accepted++;
                    await using var channel = new ProtocolChannel(transport.AsStream(), DocumentProtocol.Family, leaveOpen: true);
                    var startup = ProtocolStartupMessage.Decode((await ReadAsync(channel, token)).Payload.Span);
                    startup.Capabilities.ShouldContain(ProtocolCapability.TransactionStatus.Name);
                    await WriteAsync(channel, ProtocolMessageType.Authenticate, [], token);
                    (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.AuthenticateResponse);
                    bool legacy = index == 0 && scenario is "legacy" or "unnegotiated";
                    byte[] agreement = legacy ? scenario == "legacy" ? [] : new ProtocolReadyMessage(ProtocolVersion.Current, []).Encode()
                        : channel.Negotiate(startup.Version, startup.Capabilities).Encode();
                    await WriteAsync(channel, ProtocolMessageType.Ready, agreement, token);
                    if (index == 0)
                    {
                        (await ReadAsync(channel, token)).Type.ShouldBe((ProtocolMessageType)64);
                        await CompleteAsync(channel, 0, 0);
                        if (scenario == "missing-status") { continue; }
                        if (!legacy)
                        { await WriteAsync(channel, ProtocolMessageType.Ready, [scenario is "invalid-status" or "primary-error" ? (byte)0 : scenario == "failed" ? (byte)'E' : (byte)'T', 0], token); }
                        if (reuse || scenario.StartsWith("rollback-", StringComparison.Ordinal))
                        {
                            var rollback = await ReadAsync(channel, token);
                            rollback.Type.ShouldBe((ProtocolMessageType)64);
                            var reset = DocumentProtocolExecuteMessage.Decode(rollback.Payload.Span);
                            reset.Statement.ShouldBe("ROLLBACK");
                            reset.Parameters.ToArray().ShouldBe("{}"u8.ToArray());
                            rollbacks++;
                            if (scenario == "rollback-close") { continue; }
                            if (scenario == "rollback-error")
                            { await WriteAsync(channel, (ProtocolMessageType)72, new DocumentProtocolFailureMessage(ProtocolErrorCode.ExecutionFailure, "Reset refused.", []).Encode(), token); }
                            else if (scenario == "rollback-width")
                            { await WriteAsync(channel, (ProtocolMessageType)66, new byte[8], token); }
                            else
                            {
                                await CompleteAsync(channel, scenario == "rollback-results" ? 1 : 0, scenario == "rollback-affected" ? -1 : 0);
                                await WriteAsync(channel, ProtocolMessageType.Ready,
                                    [scenario == "rollback-active" ? (byte)'T' : scenario == "rollback-failed" ? (byte)'E' : scenario == "rollback-status" ? (byte)0 : (byte)'I', 0], token);
                            }
                        }
                    }
                    if (reuse || index == 1)
                    {
                        (await ReadAsync(channel, token)).Type.ShouldBe((ProtocolMessageType)69);
                        await CompleteAsync(channel, 0, 0);
                        await WriteAsync(channel, ProtocolMessageType.Ready, [(byte)'I', 0], token);
                    }
                    try { while (await channel.Reader.ReadFrameAsync(token) is { } frame && frame.Type != ProtocolMessageType.Terminate) { } }
                    catch (IOException) { }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        async ValueTask CompleteAsync(ProtocolChannel channel, long results, long affected)
            => await WriteAsync(channel, (ProtocolMessageType)66, new DocumentProtocolCompleteMessage(results, affected).Encode(), token);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Client] - Caller cancellation after Documents Complete preserves acknowledged COMMIT while Ready drains (#1725)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CancelAfterCommitComplete_ShouldFinishStatusWithoutCancelingCommit(bool tcp)
    {
        // Arrange: the exchange cancels exactly after decoding Complete; the peer waits for it.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var decoded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using IDuplexConnectionListener listener = tcp
            ? new TcpConnectionListener(new() { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) }) : new InMemoryConnectionListener();
        await listener.BindAsync(timeout.Token);
        await using var client = DatabaseClient.Create(new()
        {
            Family = DocumentProtocol.Family,
            Settings = new() { Database = "documents", EndPoint = listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = listener is InMemoryConnectionListener memory ? memory.CreateFactory() : new TcpConnectionFactory(),
        });
        Task peer = ServeCommitAsync();
        try
        {
            await using var connection = await client.RentAsync(timeout.Token);

            // Act / Assert
            (await connection.ExecuteAsync(new Exchange(false, completed: () => { caller.Cancel(); decoded.TrySetResult(); }), caller.Token)).ShouldBe(0);
            caller.IsCancellationRequested.ShouldBeTrue();
            connection.IsOpen.ShouldBeTrue();
            await connection.DisposeAsync();
            await using var next = await client.RentAsync(timeout.Token);
            (await next.ExecuteAsync(new Exchange(true), timeout.Token)).ShouldBe(0);
            await client.DisposeAsync();
            await next.DisposeAsync();
            await peer.WaitAsync(timeout.Token);
        }
        finally
        {
            await client.DisposeAsync();
            await timeout.CancelAsync();
            await peer;
        }

        async Task ServeCommitAsync()
        {
            try
            {
                await using var transport = await listener.AcceptAsync(timeout.Token);
                await using var channel = new ProtocolChannel(transport.AsStream(), DocumentProtocol.Family, leaveOpen: true);
                var startup = ProtocolStartupMessage.Decode((await ReadAsync(channel, timeout.Token)).Payload.Span);
                await WriteAsync(channel, ProtocolMessageType.Authenticate, [], timeout.Token);
                await ReadAsync(channel, timeout.Token);
                await WriteAsync(channel, ProtocolMessageType.Ready, channel.Negotiate(startup.Version, startup.Capabilities).Encode(), timeout.Token);
                DocumentProtocolExecuteMessage.Decode((await ReadAsync(channel, timeout.Token)).Payload.Span).Statement.ShouldBe("COMMIT");
                await WriteAsync(channel, (ProtocolMessageType)66, new DocumentProtocolCompleteMessage(0, 0).Encode(), timeout.Token);
                await decoded.Task.WaitAsync(timeout.Token);
                await WriteAsync(channel, ProtocolMessageType.Ready, [(byte)'I', 0], timeout.Token);
                (await ReadAsync(channel, timeout.Token)).Type.ShouldBe((ProtocolMessageType)69);
                await WriteAsync(channel, (ProtocolMessageType)66, new DocumentProtocolCompleteMessage(0, 0).Encode(), timeout.Token);
                await WriteAsync(channel, ProtocolMessageType.Ready, [(byte)'I', 0], timeout.Token);
                while (await channel.Reader.ReadFrameAsync(timeout.Token) is { } terminal && terminal.Type != ProtocolMessageType.Terminate) { }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }
    }

    private static async ValueTask<ProtocolFrame> ReadAsync(ProtocolChannel channel, CancellationToken token)
        => await channel.Reader.ReadFrameAsync(token) ?? throw new IOException("Scripted peer closed early.");

    private static async ValueTask WriteAsync(ProtocolChannel channel, ProtocolMessageType type, byte[] payload, CancellationToken token)
    {
        await channel.Writer.WriteFrameAsync(new(type, payload), token);
        await channel.Writer.FlushAsync(token);
    }

    private sealed class Exchange : DatabaseProtocolExchange<long>
    {
        private readonly bool _command;
        private readonly Exception? _failure;
        private readonly Action? _completed;

        internal Exchange(bool command, Exception? failure = null, Action? completed = null) : base(DocumentProtocol.Family)
        { _command = command; _failure = failure; _completed = completed; }

        protected override async ValueTask<long> ExecuteCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer, CancellationToken token)
        {
            byte[] payload = _command ? new DocumentProtocolCommandMessage(DocumentProtocolOperation.DropCollection, "items", "", null, default).Encode()
                : DocumentProtocolExecuteMessage.Create(_completed is null ? "BEGIN" : "COMMIT").Encode();
            await writer.WriteFrameAsync(new((ProtocolMessageType)(_command ? 69 : 64), payload), token);
            await writer.FlushAsync(token);
            var frame = await reader.ReadFrameAsync(token) ?? throw new IOException("No completion.");
            frame.Type.ShouldBe((ProtocolMessageType)66);
            var complete = DocumentProtocolCompleteMessage.Decode(frame.Payload.Span);
            _completed?.Invoke();
            if (_failure is not null) { MarkResponseComplete(); throw _failure; }
            return complete.AffectedCount;
        }
    }
}
