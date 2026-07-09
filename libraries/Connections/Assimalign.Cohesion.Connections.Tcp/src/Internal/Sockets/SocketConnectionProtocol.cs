using System.Net.Sockets;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Connections.Tcp.Internal;

/// <summary>
/// Maps a socket's address family to the diagnostics-only <see cref="ConnectionProtocol"/> the
/// stream driver stamps on its listeners, factories, and connections.
/// </summary>
/// <remarks>
/// The socket driver serves both TCP and Unix domain sockets through one <see cref="Socket"/>-backed
/// code path (they differ only in address family and endpoint). Honest stamping means a connection
/// bound over an <see cref="System.Net.UnixDomainSocketEndPoint"/> reports
/// <see cref="ConnectionProtocol.UnixDomainSocket"/> rather than <see cref="ConnectionProtocol.Tcp"/>
/// in its capabilities and event-source diagnostics.
/// </remarks>
internal static class SocketConnectionProtocol
{
    /// <summary>
    /// Resolves the <see cref="ConnectionProtocol"/> for the supplied socket address family.
    /// </summary>
    /// <param name="addressFamily">The address family of the bound or connected socket.</param>
    /// <returns>
    /// <see cref="ConnectionProtocol.UnixDomainSocket"/> for <see cref="AddressFamily.Unix"/>;
    /// otherwise <see cref="ConnectionProtocol.Tcp"/>.
    /// </returns>
    public static ConnectionProtocol FromAddressFamily(AddressFamily addressFamily)
        => addressFamily == AddressFamily.Unix
            ? ConnectionProtocol.UnixDomainSocket
            : ConnectionProtocol.Tcp;
}
