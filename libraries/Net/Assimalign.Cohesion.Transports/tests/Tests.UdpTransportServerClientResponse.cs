using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Assimalign.Cohesion.Transports.Tests;

public class UdpTransportServerClientResponseTests
{
    [Fact]
    public async Task RequestResponse_WhenConnectionIsOpen_ShouldExchangePayloadAsync()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        int port = GetEphemeralPort();
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);

        Task<string> serverTask = RunServerAsync(endpoint, cancellationTokenSource.Token);

        await Task.Delay(200, cancellationTokenSource.Token);

        string clientResponse = await RunClientAsync(endpoint, cancellationTokenSource.Token);
        string serverMessage = await serverTask;

        Assert.Equal("Client -> Server: Hello", serverMessage);
        Assert.Equal("Server -> Client: Hello", clientResponse);
    }

    [Fact]
    public async Task AcceptOrListenAsync_WhenMultiplePeersSendDatagrams_ShouldMaintainPeerIsolationAsync()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        int port = GetEphemeralPort();
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);

        await using UdpServerTransport server = UdpServerTransport.Create(options =>
        {
            options.EndPoint = endpoint;
        });

        await using UdpClientTransport clientA = UdpClientTransport.Create(options =>
        {
            options.EndPoint = endpoint;
        });

        await using UdpClientTransport clientB = UdpClientTransport.Create(options =>
        {
            options.EndPoint = endpoint;
        });

        await using UdpTransportConnection connectionA = await clientA.ConnectAsync(cancellationToken);
        await using UdpTransportConnection connectionB = await clientB.ConnectAsync(cancellationToken);

        UdpTransportConnectionContext contextA = await connectionA.OpenAsync(cancellationToken);
        UdpTransportConnectionContext contextB = await connectionB.OpenAsync(cancellationToken);

        Task<UdpTransportConnection> acceptTaskA = server.AcceptOrListenAsync(cancellationToken);
        Task<UdpTransportConnection> acceptTaskB = server.AcceptOrListenAsync(cancellationToken);

        await Task.Delay(100, cancellationToken);

        await contextA.Pipe.Output.WriteAsync(Encoding.UTF8.GetBytes("peer-a"), cancellationToken);
        await contextB.Pipe.Output.WriteAsync(Encoding.UTF8.GetBytes("peer-b"), cancellationToken);

        await using UdpTransportConnection serverConnectionA = await acceptTaskA;
        await using UdpTransportConnection serverConnectionB = await acceptTaskB;

        UdpTransportConnectionContext serverContextA = await serverConnectionA.OpenAsync(cancellationToken);
        UdpTransportConnectionContext serverContextB = await serverConnectionB.OpenAsync(cancellationToken);

        string serverMessageA = await ReadStringAsync(serverContextA.Pipe.Input, cancellationToken);
        string serverMessageB = await ReadStringAsync(serverContextB.Pipe.Input, cancellationToken);

        Assert.NotEqual(serverContextA.RemoteEndPoint, serverContextB.RemoteEndPoint);
        Assert.Contains("peer-a", new[] { serverMessageA, serverMessageB });
        Assert.Contains("peer-b", new[] { serverMessageA, serverMessageB });

        await serverContextA.Pipe.Output.WriteAsync(Encoding.UTF8.GetBytes($"ack:{serverMessageA}"), cancellationToken);
        await serverContextB.Pipe.Output.WriteAsync(Encoding.UTF8.GetBytes($"ack:{serverMessageB}"), cancellationToken);

        string responseA = await ReadStringAsync(contextA.Pipe.Input, cancellationToken);
        string responseB = await ReadStringAsync(contextB.Pipe.Input, cancellationToken);

        Assert.Equal("ack:peer-a", responseA);
        Assert.Equal("ack:peer-b", responseB);
    }

    private static async Task<string> RunClientAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        await using UdpClientTransport transport = UdpClientTransport.Create(options =>
        {
            options.EndPoint = endpoint;
        });

        await using UdpTransportConnection connection = await transport.ConnectAsync(cancellationToken);
        UdpTransportConnectionContext context = await connection.OpenAsync(cancellationToken);

        await context.Pipe.Output.WriteAsync(Encoding.UTF8.GetBytes("Client -> Server: Hello"), cancellationToken);

        return await ReadStringAsync(context.Pipe.Input, cancellationToken);
    }

    private static async Task<string> RunServerAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        await using UdpServerTransport transport = UdpServerTransport.Create(options =>
        {
            options.EndPoint = endpoint;
        });

        await using UdpTransportConnection connection = await transport.AcceptOrListenAsync(cancellationToken);
        UdpTransportConnectionContext context = await connection.OpenAsync(cancellationToken);

        string message = await ReadStringAsync(context.Pipe.Input, cancellationToken);

        await context.Pipe.Output.WriteAsync(Encoding.UTF8.GetBytes("Server -> Client: Hello"), cancellationToken);

        await Task.Delay(150, cancellationToken);

        return message;
    }

    private static async Task<string> ReadStringAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        ReadResult result = await reader.ReadAsync(cancellationToken);
        string value = Encoding.UTF8.GetString(result.Buffer.ToArray());
        reader.AdvanceTo(result.Buffer.End);
        return value;
    }

    private static int GetEphemeralPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        listener.Stop();

        return port;
    }
}
