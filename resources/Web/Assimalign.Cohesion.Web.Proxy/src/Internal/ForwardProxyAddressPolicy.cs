using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Proxy.Internal;

/// <summary>
/// The addresses a destination reached only through a wildcard pattern may not use, and the connect
/// step that enforces it.
/// </summary>
/// <remarks>
/// The check runs where the proxy connects, after it resolves the name itself, so the address checked
/// is the address used: a name that resolves to a public address once and to <c>127.0.0.1</c> the next
/// time (DNS rebinding) cannot get past a check made earlier.
/// </remarks>
internal static class ForwardProxyAddressPolicy
{
    /// <summary>
    /// Determines whether the policy refuses an address: loopback (<c>127.0.0.0/8</c>, <c>::1</c>),
    /// link-local (<c>169.254.0.0/16</c>, which holds cloud metadata services, and <c>fe80::/10</c>), and
    /// unspecified (<c>0.0.0.0/8</c> and <c>::</c>, which some systems connect to the local host).
    /// An IPv4 address mapped into IPv6 is judged as the IPv4 address.
    /// </summary>
    public static bool IsRefused(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        switch (address.AddressFamily)
        {
            case AddressFamily.InterNetwork:
                Span<byte> bytes = stackalloc byte[4];
                address.TryWriteBytes(bytes, out _);
                return bytes[0] == 0 || (bytes[0] == 169 && bytes[1] == 254);

            case AddressFamily.InterNetworkV6:
                return address.IsIPv6LinkLocal || address.Equals(IPAddress.IPv6Any);

            default:
                return true;
        }
    }

    /// <summary>
    /// The <see cref="SocketsHttpHandler.ConnectCallback"/> of the restricted connection pool.
    /// </summary>
    public static ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        return ConnectAsync(context.DnsEndPoint, cancellationToken);
    }

    /// <summary>
    /// Resolves the destination, drops the addresses the policy refuses, and connects to the first of
    /// the rest that accepts. With none left, it reports <c>ConnectionRefused</c> and throws a
    /// <see cref="SocketException"/> (<see cref="SocketError.AccessDenied"/>), which the forwarder and
    /// the tunnel answer with 502 Bad Gateway, as for any destination they cannot reach.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(DnsEndPoint destination, CancellationToken cancellationToken)
    {
        string host = destination.Host;
        if (host.Length > 1 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        List<IPAddress> allowed = new(addresses.Length);
        foreach (IPAddress address in addresses)
        {
            if (!IsRefused(address))
            {
                allowed.Add(address);
            }
        }

        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        if (allowed.Count == 0)
        {
            WebProxyEventSource.Log.ConnectionRefused(destination, addresses.Length);
            throw new SocketException((int)SocketError.AccessDenied);
        }

        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        Stream? stream = null;
        try
        {
            await socket.ConnectAsync(allowed.ToArray(), destination.Port, cancellationToken).ConfigureAwait(false);
            stream = new NetworkStream(socket, ownsSocket: true);
            return stream;
        }
        finally
        {
            if (stream is null)
            {
                socket.Dispose();
            }
        }
    }
}
