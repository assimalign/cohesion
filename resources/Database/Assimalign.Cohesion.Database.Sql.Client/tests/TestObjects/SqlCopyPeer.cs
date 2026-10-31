using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Protocol;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

internal sealed class SqlCopyPeer : IAsyncDisposable
{
    private readonly InMemoryConnectionListener _listener = new();
    private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(30));
    private readonly Task _serving;
    internal SqlCopyPeer(string response)
    {
        Client = SqlClient.Create(new SqlClientOptions { ConnectionFactory = _listener.CreateFactory(),
            Settings = new DatabaseConnectionSettings { Database = "app", EndPoint = _listener.EndPoint, Principal = "test" } });
        _serving = ServeAsync(response);
    }
    internal SqlClient Client { get; }
    internal int Requests { get; private set; }
    private async Task ServeAsync(string response)
    {
        try
        {
            await using var transport = await _listener.AcceptAsync(_lifetime.Token);
            await using var channel = new ProtocolChannel(transport.AsStream(), SqlProtocol.Family, true);
            _ = await channel.Reader.ReadFrameAsync(_lifetime.Token);
            await Send(ProtocolMessageType.Authenticate, ReadOnlyMemory<byte>.Empty);
            _ = await channel.Reader.ReadFrameAsync(_lifetime.Token);
            await Send(ProtocolMessageType.Ready, channel.Negotiate(response == "legacy" ? new ProtocolVersion(2, 0) : ProtocolVersion.Current,
                response == "legacy" ? [] : response == "bad-ready"
                    ? [SqlProtocol.OutboundStream.Name, ProtocolCapability.TransactionStatus.Name]
                    : [SqlProtocol.OutboundStream.Name]).Encode());
            while (await channel.Reader.ReadFrameAsync(_lifetime.Token) is { } request)
            {
                if (request.Type == ProtocolMessageType.Terminate) { return; }
                Requests++;
                if (response == "legacy")
                {
                    await Send((ProtocolMessageType)8, new ProtocolResultCompleteMessage(0).Encode());
                    continue;
                }
                if (response == "bad-start") { await Send((ProtocolMessageType)81, new byte[] {1}); return; }
                await Send((ProtocolMessageType)81, ReadOnlyMemory<byte>.Empty);
                if (response == "unexpected-ack") { await Send((ProtocolMessageType)84, ReadOnlyMemory<byte>.Empty); return; }
                if (response == "oversize") { await Send((ProtocolMessageType)82, new byte[16385]); return; }
                if (response == "empty") { await Send((ProtocolMessageType)82, ReadOnlyMemory<byte>.Empty); return; }
                if (response == "truncated") { return; }
                await Send((ProtocolMessageType)82, new byte[] {(byte)'1', (byte)'\n'});
                if (response == "error-before-ack")
                {
                    await Send(ProtocolMessageType.Error, new ProtocolErrorMessage(ProtocolErrorCode.ExecutionFailure, "CS05007: injected cast failure.").Encode());
                }
                var acknowledged = await channel.Reader.ReadFrameAsync(_lifetime.Token);
                if (acknowledged is not { } ack || ack.Type != (ProtocolMessageType)SqlProtocolMessageType.CopyAcknowledged || !ack.Payload.IsEmpty)
                { throw new InvalidOperationException("Expected COPY delivery acknowledgment."); }
                if (response == "error-before-ack") { return; }
                if (response is "error" or "bad-ready")
                {
                    await Send(ProtocolMessageType.Error, new ProtocolErrorMessage(ProtocolErrorCode.ExecutionFailure, "CS05007: injected cast failure.").Encode());
                    if (response == "bad-ready") { await Send(ProtocolMessageType.Ready, new byte[] {(byte)'I'}); }
                    return;
                }
                await Send((ProtocolMessageType)83, response == "bad-complete" ? new byte[15] : new ProtocolCopyCompleteMessage(1, 3).Encode());
                return;
            }
            async ValueTask Send(ProtocolMessageType type, ReadOnlyMemory<byte> payload)
            {
                await channel.Writer.WriteFrameAsync(new ProtocolFrame(type, payload), _lifetime.Token);
                await channel.Writer.FlushAsync(_lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ConnectionAbortedException) { }
    }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await Client.DisposeAsync();
        await _listener.DisposeAsync();
        await _serving;
        _lifetime.Dispose();
    }
}
