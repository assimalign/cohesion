using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql;

namespace Assimalign.Cohesion.Database.Client.Tests;

public class DatabaseProtocolExchangeTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Client] - Exchange: generic framed operations reuse the authenticated session")]
    public async Task ExecuteAsync_Ping_ShouldCompleteWithoutResultMaterialization()
    {
        await using var harness = await ClientTestHarness.StartAsync();
        await using var connection = await harness.Client.RentAsync(ClientTestHarness.Timeout());

        ProtocolMessageType response = await connection.ExecuteAsync(new PingExchange(SqlProtocol.Family), ClientTestHarness.Timeout());

        response.ShouldBe(ProtocolMessageType.Pong);
        connection.IsOpen.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Exchange: another family is rejected before any bytes are sent")]
    public async Task ExecuteAsync_DifferentFamily_ShouldRejectAndPreserveConnection()
    {
        await using var harness = await ClientTestHarness.StartAsync();
        await using var connection = await harness.Client.RentAsync(ClientTestHarness.Timeout());
        var other = new PingExchange(new ProtocolMessageFamily("another-model", 5, 6, 7, 8, 9));

        await Should.ThrowAsync<ArgumentException>(async () => await connection.ExecuteAsync(other, ClientTestHarness.Timeout()));

        other.Started.ShouldBeFalse();
        connection.IsOpen.ShouldBeTrue();
        (await connection.ExecuteAsync(new PingExchange(SqlProtocol.Family), ClientTestHarness.Timeout())).ShouldBe(ProtocolMessageType.Pong);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Exchange: cancellation discards a connection with an unread response")]
    public async Task ExecuteAsync_CanceledExchange_ShouldPreventSessionReuse()
    {
        await using var harness = await ClientTestHarness.StartAsync();
        var connection = await harness.Client.RentAsync(ClientTestHarness.Timeout());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await connection.ExecuteAsync(new InterruptedExchange(cancellation), cancellation.Token));

        connection.IsOpen.ShouldBeFalse();
        await connection.DisposeAsync();
        await using var replacement = await harness.Client.RentAsync(ClientTestHarness.Timeout());
        replacement.ShouldNotBeSameAs(connection);
        (await replacement.ExecuteAsync(new PingExchange(SqlProtocol.Family), ClientTestHarness.Timeout())).ShouldBe(ProtocolMessageType.Pong);
    }

    private sealed class PingExchange : IDatabaseProtocolExchange<ProtocolMessageType>
    {
        private readonly ProtocolMessageFamily _family;

        /// <summary>Initializes a new instance of the <see cref="PingExchange"/> class.</summary>
        /// <param name="family">The protocol message family the exchange declares.</param>
        public PingExchange(ProtocolMessageFamily family)
        {
            _family = family;
        }

        public ProtocolMessageFamily Family => _family;
        internal bool Started { get; private set; }

        public async ValueTask<ProtocolMessageType> ExecuteAsync(IProtocolFrameReader reader, IProtocolFrameWriter writer, CancellationToken cancellationToken = default)
        {
            Started = true;
            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Ping, ReadOnlyMemory<byte>.Empty), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            ProtocolFrame? frame = await reader.ReadFrameAsync(cancellationToken);
            return frame?.Type ?? throw new ProtocolException("The connection closed before Pong.");
        }
    }

    private sealed class InterruptedExchange : IDatabaseProtocolExchange<bool>
    {
        private readonly CancellationTokenSource _cancellation;

        /// <summary>Initializes a new instance of the <see cref="InterruptedExchange"/> class.</summary>
        /// <param name="cancellation">The cancellation source the exchange cancels after sending its request.</param>
        public InterruptedExchange(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public ProtocolMessageFamily Family => SqlProtocol.Family;

        public async ValueTask<bool> ExecuteAsync(IProtocolFrameReader reader, IProtocolFrameWriter writer, CancellationToken cancellationToken = default)
        {
            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Ping, ReadOnlyMemory<byte>.Empty), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            _cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
    }
}
