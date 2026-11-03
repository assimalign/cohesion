using System;
using System.Globalization;
using System.Net;

using Assimalign.Cohesion.Connections.Tcp;

// Deviates from the repo RootNamespace rule: this stateless helper is compiled into Database and Database.Tcp (general-rules.md, shared source).
namespace Assimalign.Cohesion.Database.Internal;

internal static class DatabaseTcpEndpoint
{
    public static TcpConnectionListener CreateListener(Uri endpoint)
    {
        Uri.ThrowIfNotEndpoint(endpoint);
        IPAddress address = ResolveHost(endpoint.IdnHost);
        return TcpConnectionListener.Create(tcp => tcp.EndPoint = new IPEndPoint(address, endpoint.Port));
    }

    private static IPAddress ResolveHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        if (host is "*" or "+" or "0.0.0.0")
        {
            return IPAddress.Any;
        }

        if (host is "[::]" or "::")
        {
            return IPAddress.IPv6Any;
        }

        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            return address;
        }

        throw new InvalidOperationException(
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.EndpointHostUnsupported, host));
    }
}
