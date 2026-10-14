using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.WebSockets.Tests.TestObjects;

/// <summary>
/// Two connected loopback TCP streams: what the server end of an accepted handshake writes, the
/// client end reads, and the other way round. A WebSocket framed over one is driven from the other.
/// </summary>
internal sealed class LoopbackStreamPair : IAsyncDisposable
{
    private LoopbackStreamPair(Stream server, Stream client)
    {
        Server = server;
        Client = client;
    }

    public Stream Server { get; }

    public Stream Client { get; }

    public static async Task<LoopbackStreamPair> CreateAsync(CancellationToken cancellationToken)
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();

        Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        Task connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, cancellationToken).AsTask();
        Socket server = await listener.AcceptSocketAsync(cancellationToken);
        await connect;
        server.NoDelay = true;

        return new LoopbackStreamPair(new NetworkStream(server, ownsSocket: true), new NetworkStream(client, ownsSocket: true));
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        await Client.DisposeAsync();
    }
}
