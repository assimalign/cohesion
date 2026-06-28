using System.IO.Pipelines;
using System.Net;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Connections.Security.Tests;

/// <summary>
/// Creates a connected in-memory pair of <see cref="TestPipeConnection"/> instances cross-wired
/// over two <see cref="Pipe"/> instances: bytes written to the client's output arrive on the
/// server's input and vice versa, exactly like the two ends of a real transport connection.
/// </summary>
internal static class InMemoryConnectionPair
{
    public static (TestPipeConnection Client, TestPipeConnection Server) Create(
        ConnectionCapabilities? capabilities = null)
    {
        Pipe clientToServer = new(new PipeOptions(useSynchronizationContext: false));
        Pipe serverToClient = new(new PipeOptions(useSynchronizationContext: false));

        IPEndPoint clientEndPoint = new(IPAddress.Loopback, 21000);
        IPEndPoint serverEndPoint = new(IPAddress.Loopback, 22000);

        TestPipeConnection client = new(
            serverToClient.Reader,
            clientToServer.Writer,
            localEndPoint: clientEndPoint,
            remoteEndPoint: serverEndPoint,
            capabilities);

        TestPipeConnection server = new(
            clientToServer.Reader,
            serverToClient.Writer,
            localEndPoint: serverEndPoint,
            remoteEndPoint: clientEndPoint,
            capabilities);

        return (client, server);
    }
}
