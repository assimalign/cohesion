using System;
using System.Collections.Generic;
using System.Net;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// Represents the UDP connection context that contains endpoint metadata and the connection pipe.
/// </summary>
public sealed class UdpTransportConnectionContext : ITransportConnectionContext
{
    private readonly Dictionary<string, object?> _items;

    internal UdpTransportConnectionContext(EndPoint localEndPoint, EndPoint remoteEndPoint, ITransportConnectionPipe pipe)
    {
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
        Pipe = pipe;
        _items = new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public EndPoint LocalEndPoint { get; }

    /// <inheritdoc />
    public EndPoint RemoteEndPoint { get; }

    /// <inheritdoc />
    public ITransportConnectionPipe Pipe { get; private set; }

    /// <inheritdoc />
    public IDictionary<string, object?> Items => _items;

    /// <summary>
    /// Replaces the active connection pipe with a custom implementation.
    /// </summary>
    /// <param name="pipe">The replacement connection pipe.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pipe"/> is <see langword="null"/>.</exception>
    public void SetPipe(ITransportConnectionPipe pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        Pipe = pipe;
    }
}
