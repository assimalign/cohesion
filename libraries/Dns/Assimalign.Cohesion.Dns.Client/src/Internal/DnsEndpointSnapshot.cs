using System.Net;
using System.Net.Sockets;

namespace Assimalign.Cohesion.Dns.Internal;

internal static class DnsEndpointSnapshot
{
    internal static EndPoint Copy(EndPoint endpoint)
        => endpoint is IPEndPoint ip ? Copy(ip) : endpoint;

    internal static IPEndPoint Copy(IPEndPoint endpoint)
    {
        IPAddress address = endpoint.Address.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPAddress(endpoint.Address.GetAddressBytes(), endpoint.Address.ScopeId)
            : new IPAddress(endpoint.Address.GetAddressBytes());
        return new IPEndPoint(address, endpoint.Port);
    }
}
