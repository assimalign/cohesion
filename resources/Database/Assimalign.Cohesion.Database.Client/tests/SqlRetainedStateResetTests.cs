using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Protocol;

namespace Assimalign.Cohesion.Database.Client.Tests;

public sealed class SqlRetainedStateResetTests
{
    private readonly ITestOutputHelper _output;
    public SqlRetainedStateResetTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Database.Client] - Scripted pool reset: exact exchange counts follow Ready state")]
    [InlineData(false, "idle", 0)][InlineData(true, "idle", 0)]
    [InlineData(false, "select", 1)][InlineData(true, "select", 1)]
    [InlineData(false, "transaction", 2)][InlineData(true, "transaction", 2)]
    [InlineData(false, "nextval", 2)][InlineData(true, "nextval", 2)]
    [InlineData(false, "discarded", 2)][InlineData(true, "discarded", 2)]
    [InlineData(false, "transaction-nextval", 4)][InlineData(true, "transaction-nextval", 4)]
    [InlineData(false, "declared-reset", 2)][InlineData(true, "declared-reset", 2)]
    public async Task ReturnAsync_ReadyState_ShouldResetOnlyWhenRequired(bool tcp, string scenario, int expected)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using IDuplexConnectionListener listener = tcp
            ? TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            : new InMemoryConnectionListener();
        await listener.BindAsync(timeout.Token);
        var family = scenario == "declared-reset"
            ? new ProtocolMessageFamily("Alternate", [ProtocolCapability.TransactionStatus], "CLEAR SESSION", 5, 6, 7, 8, 9)
            : SqlProtocol.Family;
        await using var client = DatabaseClient.Create(new()
        {
            Family = family,
            Settings = new() { Database = "sql", EndPoint = listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = tcp ? new TcpConnectionFactory() : ((InMemoryConnectionListener)listener).CreateFactory(),
        });
        var requests = new ConcurrentQueue<string>();
        Task peer = ServeAsync();
        try
        {
            // Act
            await using (var lease = await client.RentAsync(timeout.Token))
            {
                if (scenario == "select") { await lease.ExecuteAsync(new StatementExchange(family, "SELECT 1"), timeout.Token); }
                if (scenario is "transaction" or "transaction-nextval") { await lease.ExecuteAsync(new StatementExchange(family, "BEGIN"), timeout.Token); }
                if (scenario is "nextval" or "discarded" or "transaction-nextval" or "declared-reset")
                { await lease.ExecuteAsync(new StatementExchange(family, "SELECT nextval('s')"), timeout.Token); }
                if (scenario == "discarded") { await lease.ExecuteAsync(new StatementExchange(family, "DISCARD ALL"), timeout.Token); }
            }

            // Assert
            _output.WriteLine($"transport={(tcp ? "TCP" : "InMemory")}; lease={scenario}; exchanges={requests.Count}; statements={string.Join(",", requests)}");
            requests.Count.ShouldBe(expected);
            if (scenario == "transaction-nextval")
            { requests.ToArray().ShouldBe(new[] { "BEGIN", "SELECT nextval('s')", "ROLLBACK", "DISCARD ALL" }); }
            if (scenario == "declared-reset") { requests.Last().ShouldBe("CLEAR SESSION"); }
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
                await using var channel = new ProtocolChannel(transport.AsStream(), family);
                var startup = ProtocolStartupMessage.Decode((await channel.Reader.ReadFrameAsync(timeout.Token))!.Value.Payload.Span);
                await Send(ProtocolMessageType.Authenticate, []);
                (await channel.Reader.ReadFrameAsync(timeout.Token))!.Value.Type.ShouldBe(ProtocolMessageType.AuthenticateResponse);
                await Send(ProtocolMessageType.Ready, channel.Negotiate(startup.Version, startup.Capabilities).Encode());
                bool active = false, retained = false;
                while (await channel.Reader.ReadFrameAsync(timeout.Token) is { } frame)
                {
                    if (frame.Type == ProtocolMessageType.Terminate) { break; }
                    frame.Type.ShouldBe((ProtocolMessageType)5);
                    string sql = ProtocolExecuteMessage.Decode(frame.Payload.Span).Statement;
                    requests.Enqueue(sql);
                    if (sql == "BEGIN") { active = true; }
                    if (sql == "ROLLBACK") { active = false; }
                    if (sql == "SELECT nextval('s')") { retained = true; }
                    if (sql == family.ResetStatement) { retained = false; }
                    await Send((ProtocolMessageType)8, new ProtocolResultCompleteMessage(0).Encode());
                    await Send(ProtocolMessageType.Ready, new ProtocolTransactionStatusMessage(
                        active ? ProtocolTransactionStatus.Active : ProtocolTransactionStatus.Idle, retained).Encode());
                }

                async Task Send(ProtocolMessageType type, byte[] payload)
                {
                    await channel.Writer.WriteFrameAsync(new(type, payload), timeout.Token);
                    await channel.Writer.FlushAsync(timeout.Token);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            catch (IOException) when (timeout.IsCancellationRequested) { }
        }
    }

    private sealed class StatementExchange : DatabaseProtocolExchange<bool>
    {
        private readonly string _statement;
        internal StatementExchange(ProtocolMessageFamily family, string statement) : base(family, transactionStatus: true)
        { _statement = statement; }

        protected override async ValueTask<bool> ExecuteCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer, CancellationToken cancellationToken)
        {
            await writer.WriteFrameAsync(new((ProtocolMessageType)5, ProtocolExecuteMessage.Create(_statement).Encode()), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            var frame = await reader.ReadFrameAsync(cancellationToken);
            frame.ShouldNotBeNull().Type.ShouldBe((ProtocolMessageType)8);
            MarkResponseComplete();
            return true;
        }
    }
}
