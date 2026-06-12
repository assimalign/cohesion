using System.Net;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Represents the result of receiving a single datagram.
/// </summary>
public readonly struct DatagramReceiveResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DatagramReceiveResult"/> struct.
    /// </summary>
    /// <param name="received">The number of bytes written into the receive buffer.</param>
    /// <param name="remoteEndPoint">The endpoint the datagram was received from.</param>
    public DatagramReceiveResult(int received, EndPoint remoteEndPoint)
    {
        Received = received;
        RemoteEndPoint = remoteEndPoint;
    }

    /// <summary>
    /// Gets the number of bytes written into the receive buffer.
    /// </summary>
    public int Received { get; }

    /// <summary>
    /// Gets the endpoint the datagram was received from.
    /// </summary>
    public EndPoint RemoteEndPoint { get; }
}
