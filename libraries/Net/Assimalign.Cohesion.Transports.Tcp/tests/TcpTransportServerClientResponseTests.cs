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

public class TcpTransportServerClientResponseTests
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

    private static async Task<string> RunClientAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        await using TcpClientTransport transport = TcpClientTransport.Create(options =>
        {
            options.EndPoint = endpoint;
        });

        await using TcpTransportConnection connection = await transport.ConnectAsync(cancellationToken);
        TcpTransportConnectionContext context = await connection.OpenAsync(cancellationToken);

        await context.Pipe.Output.WriteAsync(Encoding.UTF8.GetBytes("Client -> Server: Hello"), cancellationToken);

        ReadResult result = await context.Pipe.Input.ReadAsync(cancellationToken);
        string response = Encoding.UTF8.GetString(result.Buffer.ToArray());
        context.Pipe.Input.AdvanceTo(result.Buffer.End);

        return response;
    }

    private static async Task<string> RunServerAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        await using TcpServerTransport transport = TcpServerTransport.Create(options =>
        {
            options.EndPoint = endpoint;
        });

        await using TcpTransportConnection connection = await transport.AcceptOrListenAsync(cancellationToken);
        TcpTransportConnectionContext context = await connection.OpenAsync(cancellationToken);

        ReadResult result = await context.Pipe.Input.ReadAsync(cancellationToken);
        string message = Encoding.UTF8.GetString(result.Buffer.ToArray());
        context.Pipe.Input.AdvanceTo(result.Buffer.End);

        await context.Pipe.Output.WriteAsync(Encoding.UTF8.GetBytes("Server -> Client: Hello"), cancellationToken);

        await Task.Delay(100, cancellationToken);

        return message;
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
