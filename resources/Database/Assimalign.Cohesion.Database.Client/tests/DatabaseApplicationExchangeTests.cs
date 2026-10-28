using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Protocol;

namespace Assimalign.Cohesion.Database.Client.Tests;

public sealed class DatabaseApplicationExchangeTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Client] - Application Execute exchanges drain negotiated status before the next exchange (w11 review)")]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public async Task ExecuteAsync_ApplicationExchange_ShouldConsumeStatusDespiteDefaultConstructor(bool cancelAfterResult, bool failAfterResult, bool paths)
    {
        // Arrange: the exchange cancels at the exact result/status boundary, without a timing race.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var caller = new CancellationTokenSource();
        await using var listener = new InMemoryConnectionListener();
        await using var client = DatabaseClient.Create(new()
        {
            Family = paths ? GraphProtocol.Family : SqlProtocol.Family,
            Settings = new() { Database = "sql", EndPoint = listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = listener.CreateFactory(),
        });
        Task peer = ServeAsync();
        try
        {
            await using var connection = await client.RentAsync(timeout.Token);
            var exchange = cancelAfterResult ? new ApplicationExchange(caller) : new ApplicationExchange(failAfterResult, paths);

            // Act
            if (failAfterResult)
            {
                await Should.ThrowAsync<InvalidOperationException>(async () => await connection.ExecuteAsync(exchange, caller.Token));
            }
            else { (await connection.ExecuteAsync(exchange, caller.Token)).ShouldBe(0); }

            // Assert: a completed COMMIT remains successful even if cancellation fires before Ready.
            connection.IsOpen.ShouldBeTrue();
            (await connection.ExecuteAsync(new ApplicationExchange(false, paths), timeout.Token)).ShouldBe(0);
        }
        finally
        {
            await client.DisposeAsync();
            timeout.Cancel();
            await peer;
        }

        async Task ServeAsync()
        {
            try
            {
                await using var transport = await listener.AcceptAsync(timeout.Token);
                await using var channel = new ProtocolChannel(transport.AsStream(), paths ? GraphProtocol.Family : SqlProtocol.Family);
                var startup = ProtocolStartupMessage.Decode((await channel.Reader.ReadFrameAsync(timeout.Token))!.Value.Payload.Span);
                await SendAsync(ProtocolMessageType.Authenticate, []);
                (await channel.Reader.ReadFrameAsync(timeout.Token))!.Value.Type.ShouldBe(ProtocolMessageType.AuthenticateResponse);
                await SendAsync(ProtocolMessageType.Ready, channel.Negotiate(startup.Version, startup.Capabilities).Encode());
                for (int i = 0; i < 2; i++)
                {
                    var next = await channel.Reader.ReadFrameAsync(timeout.Token);
                    if (next is null || next.Value.Type == ProtocolMessageType.Terminate) { return; }
                    var request = next.Value;
                    request.Type.ShouldBe((ProtocolMessageType)(paths ? 64 : 5));
                    ProtocolExecuteMessage.Decode(request.Payload.Span).Statement.ShouldBe(paths ? "MATCH (n) RETURN n" : "COMMIT");
                    await SendAsync((ProtocolMessageType)(paths ? 66 : 8), new ProtocolResultCompleteMessage(0).Encode());
                    await SendAsync(ProtocolMessageType.Ready, [(byte)'I']);
                }
                if (!paths)
                {
                    // Returning the SQL lease certifies a fresh logical session over Execute.
                    var reset = (await channel.Reader.ReadFrameAsync(timeout.Token))!.Value;
                    reset.Type.ShouldBe((ProtocolMessageType)5);
                    ProtocolExecuteMessage.Decode(reset.Payload.Span).Statement.ShouldBe("DISCARD ALL");
                    await SendAsync((ProtocolMessageType)8, new ProtocolResultCompleteMessage(0).Encode());
                    await SendAsync(ProtocolMessageType.Ready, [(byte)'I']);
                }
                (await channel.Reader.ReadFrameAsync(timeout.Token))!.Value.Type.ShouldBe(ProtocolMessageType.Terminate);

                async Task SendAsync(ProtocolMessageType type, byte[] payload)
                {
                    await channel.Writer.WriteFrameAsync(new(type, payload), timeout.Token);
                    await channel.Writer.FlushAsync(timeout.Token);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }
    }

    private sealed class ApplicationExchange : DatabaseProtocolExchange<long>
    {
        private readonly CancellationTokenSource? _caller;
        private readonly bool _fail;
        private readonly bool _paths;

        internal ApplicationExchange(bool fail, bool paths) : base(paths ? GraphProtocol.Family : SqlProtocol.Family)
        {
            _fail = fail;
            _paths = paths;
        }

        internal ApplicationExchange(CancellationTokenSource caller) : base(SqlProtocol.Family, transactionStatus: true)
        {
            _caller = caller;
        }

        protected override async ValueTask<long> ExecuteCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer, CancellationToken token)
        {
            await writer.WriteFrameAsync(new((ProtocolMessageType)(_paths ? 64 : 5),
                ProtocolExecuteMessage.Create(_paths ? "MATCH (n) RETURN n" : "COMMIT").Encode()), token);
            await writer.FlushAsync(token);
            var result = (await reader.ReadFrameAsync(token))!.Value;
            result.Type.ShouldBe((ProtocolMessageType)(_paths ? 66 : 8));
            long count = ProtocolResultCompleteMessage.Decode(result.Payload.Span).AffectedCount;
            _caller?.Cancel();
            if (_fail)
            {
                MarkResponseComplete();
                throw new InvalidOperationException("Completed application statement.");
            }
            return count;
        }
    }
}
