using System.Net;

using Assimalign.Cohesion.Connections.InMemory.Internal;

namespace Assimalign.Cohesion.Connections.InMemory;

/// <summary>
/// Creates cross-wired pairs of in-memory <see cref="Connection"/> instances for socketless testing.
/// </summary>
/// <remarks>
/// The two returned connections are the two ends of a single duplex channel: bytes written to the
/// client's <see cref="Connection.Output"/> arrive on the server's <see cref="Connection.Input"/> and
/// vice versa, so a test can drive a live, multi-round-trip exchange with no operating-system socket.
/// This is the shared primitive that consolidates the previously duplicated pipe-pair connection
/// doubles across the transport test projects.
/// </remarks>
public static class InMemoryConnectionPair
{
    /// <summary>
    /// The default capabilities of an in-memory connection: a reliable, ordered, single-stream,
    /// unsecured byte channel advertising <see cref="ConnectionProtocol.Memory"/>.
    /// </summary>
    public static ConnectionCapabilities DefaultCapabilities { get; } = new(
        ConnectionProtocol.Memory,
        ConnectionDelivery.Stream,
        IsReliable: true,
        IsOrdered: true,
        IsMultiplexed: false,
        Security: ConnectionSecurity.None);

    /// <summary>
    /// Creates a cross-wired pair of connected in-memory connections.
    /// </summary>
    /// <param name="capabilities">
    /// The capabilities both ends advertise, or <see langword="null"/> to use <see cref="DefaultCapabilities"/>.
    /// </param>
    /// <param name="clientEndPoint">
    /// The client's local endpoint (and the server's remote endpoint), or <see langword="null"/> to mint
    /// an ephemeral in-memory endpoint.
    /// </param>
    /// <param name="serverEndPoint">
    /// The server's local endpoint (and the client's remote endpoint), or <see langword="null"/> to use
    /// the default in-memory endpoint.
    /// </param>
    /// <returns>
    /// The <c>Client</c> and <c>Server</c> ends of the connection. Both are live and bidirectional on return.
    /// </returns>
    public static (Connection Client, Connection Server) Create(
        ConnectionCapabilities? capabilities = null,
        EndPoint? clientEndPoint = null,
        EndPoint? serverEndPoint = null)
    {
        ConnectionCapabilities effectiveCapabilities = capabilities ?? DefaultCapabilities;
        EndPoint client = clientEndPoint ?? InMemoryEndPoint.CreateEphemeral();
        EndPoint server = serverEndPoint ?? new InMemoryEndPoint(InMemoryEndPoint.DefaultName);

        (InMemoryConnection clientConnection, InMemoryConnection serverConnection) = InMemoryConnection.CreatePair(
            effectiveCapabilities,
            endPointA: client,
            endPointB: server);

        return (clientConnection, serverConnection);
    }
}
