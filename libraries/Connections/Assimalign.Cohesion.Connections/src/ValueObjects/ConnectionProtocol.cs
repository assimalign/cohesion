namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Identifies the underlying network protocol of a transport.
/// </summary>
/// <remarks>
/// This value is for diagnostics and observability only. Consumers must not branch behavior on
/// the protocol identity; gate on <see cref="ConnectionCapabilities"/> instead (for example, require
/// <see cref="ConnectionCapabilities.IsReliable"/> and <see cref="ConnectionCapabilities.IsOrdered"/>
/// rather than checking for <see cref="Tcp"/> or <see cref="Quic"/>).
/// </remarks>
public partial struct ConnectionProtocol
{
    /// <summary>
    /// The Transmission Control Protocol (TCP).
    /// </summary>
    public static ConnectionProtocol Tcp => "Tcp";

    /// <summary>
    /// The User Datagram Protocol (UDP).
    /// </summary>
    public static ConnectionProtocol Udp => "Udp";

    /// <summary>
    /// The QUIC transport protocol.
    /// </summary>
    public static ConnectionProtocol Quic => "Quic";

    /// <summary>
    /// A Unix domain socket.
    /// </summary>
    public static ConnectionProtocol UnixDomainSocket => "UnixDomainSocket";

    /// <summary>
    /// A named pipe.
    /// </summary>
    public static ConnectionProtocol NamedPipe => "NamedPipe";

    /// <summary>
    /// An in-memory transport, typically used for testing.
    /// </summary>
    public static ConnectionProtocol Memory => "Memory";

    /// <summary>
    /// An unspecified or unknown protocol.
    /// </summary>
    public static ConnectionProtocol Unspecified => "Unspecified";
}
