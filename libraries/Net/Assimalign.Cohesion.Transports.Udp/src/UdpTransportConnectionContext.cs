using System;
using System.Net;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// Represents the UDP connection context that contains endpoint metadata and the connection pipe.
/// </summary>
public sealed class UdpTransportConnectionContext : TransportConnectionContext
{
    private ITransportConnectionPipe _pipe;

    internal UdpTransportConnectionContext(EndPoint localEndPoint, EndPoint remoteEndPoint, ITransportConnectionPipe pipe)
    {
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
        _pipe = pipe;
    }

    /// <inheritdoc />
    public override EndPoint LocalEndPoint { get; }

    /// <inheritdoc />
    public override EndPoint RemoteEndPoint { get; }

    /// <inheritdoc />
    public override ITransportConnectionPipe Pipe => _pipe;


    /// <summary>
    /// Replaces the active connection pipe with a custom implementation.
    /// </summary>
    /// <param name="pipe">The replacement connection pipe.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pipe"/> is <see langword="null"/>.</exception>
    public void SetPipe(ITransportConnectionPipe pipe)
    {
        _pipe = ArgumentNullException.ThrowIfNull<ITransportConnectionPipe>(pipe);
    }
}
