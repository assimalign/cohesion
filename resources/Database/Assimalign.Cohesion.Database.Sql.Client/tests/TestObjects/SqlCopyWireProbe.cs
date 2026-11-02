using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Protocol;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

internal sealed class SqlCopyWireProbe : IAsyncDisposable
{
    private readonly IDuplexConnection _transport;
    private readonly ProtocolChannel _channel;
    private SqlCopyWireProbe(IDuplexConnection transport)
    {
        _transport = transport;
        _channel = new ProtocolChannel(transport.AsStream(), SqlProtocol.Family, true);
    }

    internal static async Task<SqlCopyWireProbe> OpenAsync(SqlClientTestHarness harness, bool tcp, CancellationToken cancellationToken)
    {
        IDuplexConnectionFactory factory = tcp ? new TcpConnectionFactory() : ((InMemoryConnectionListener)harness.Listener).CreateFactory();
        var probe = new SqlCopyWireProbe(await factory.ConnectAsync(harness.Listener.EndPoint, cancellationToken));
        try
        {
            await probe.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "app", "probe")
                { Capabilities = [SqlProtocol.OutboundStream.Name, ProtocolCapability.Diagnostics.Name] }.Encode(), cancellationToken);
            (await probe.ReadAsync(cancellationToken)).Type.ShouldBe(ProtocolMessageType.Authenticate);
            await probe.SendAsync(ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty, cancellationToken);
            var ready = await probe.ReadAsync(cancellationToken);
            ready.Type.ShouldBe(ProtocolMessageType.Ready);
            probe._channel.SetAgreement(ProtocolReadyMessage.Decode(ready.Payload.Span));
            return probe;
        }
        catch { await probe.DisposeAsync(); throw; }
    }

    internal async ValueTask SendAsync(ProtocolMessageType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _channel.Writer.WriteFrameAsync(new(type, payload), cancellationToken);
        await _channel.Writer.FlushAsync(cancellationToken);
    }

    internal async ValueTask<ProtocolFrame> ReadAsync(CancellationToken cancellationToken)
        => await _channel.Reader.ReadFrameAsync(cancellationToken) ?? throw new InvalidOperationException("Missing COPY probe response.");

    internal ValueTask CloseOutputAsync() => _transport.Output.CompleteAsync();

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _transport.DisposeAsync();
    }
}
