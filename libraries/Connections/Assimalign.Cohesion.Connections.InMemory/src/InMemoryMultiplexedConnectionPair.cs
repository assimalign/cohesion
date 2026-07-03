using System.Net;

using Assimalign.Cohesion.Connections.InMemory.Internal;

namespace Assimalign.Cohesion.Connections.InMemory;

/// <summary>
/// Creates cross-wired pairs of in-memory <see cref="MultiplexedConnection"/> instances for
/// h2/h3-shaped, socketless stream tests.
/// </summary>
/// <remarks>
/// A stream opened on one end is delivered to the other end's accept queue, so a test can drive
/// multiple concurrent request/response streams over a single connection with no operating-system socket.
/// </remarks>
public static class InMemoryMultiplexedConnectionPair
{
    /// <summary>
    /// The default capabilities of an in-memory multiplexed connection: a reliable, ordered,
    /// multiplexed, unsecured byte channel advertising <see cref="ConnectionProtocol.Memory"/>.
    /// </summary>
    public static ConnectionCapabilities DefaultCapabilities { get; } =
        InMemoryConnectionPair.DefaultCapabilities with { IsMultiplexed = true };

    /// <summary>
    /// Creates a cross-wired pair of connected in-memory multiplexed connections.
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
    /// <returns>The <c>Client</c> and <c>Server</c> ends of the multiplexed connection.</returns>
    public static (MultiplexedConnection Client, MultiplexedConnection Server) Create(
        ConnectionCapabilities? capabilities = null,
        EndPoint? clientEndPoint = null,
        EndPoint? serverEndPoint = null)
    {
        ConnectionCapabilities effectiveCapabilities = capabilities ?? DefaultCapabilities;
        EndPoint client = clientEndPoint ?? InMemoryEndPoint.CreateEphemeral();
        EndPoint server = serverEndPoint ?? new InMemoryEndPoint(InMemoryEndPoint.DefaultName);

        (InMemoryMultiplexedConnection clientConnection, InMemoryMultiplexedConnection serverConnection) =
            InMemoryMultiplexedConnection.CreatePair(effectiveCapabilities, endPointA: client, endPointB: server);

        return (clientConnection, serverConnection);
    }
}
