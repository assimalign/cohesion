namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Describes how a transport delivers data.
/// </summary>
public enum ConnectionDelivery
{
    /// <summary>
    /// A continuous byte stream (for example, a TCP connection or a QUIC stream).
    /// </summary>
    Stream = 0,

    /// <summary>
    /// Discrete, message-oriented datagrams (for example, UDP).
    /// </summary>
    Datagram = 1
}
