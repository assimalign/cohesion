using System;

namespace Assimalign.Cohesion.Http.Transports;

using Assimalign.Cohesion.Transports;

/// <summary>
/// 
/// </summary>
public abstract class HttpConnectionTransport : ServerTransport
{
    /// <summary>
    /// 
    /// </summary>
    public abstract bool IsSecure { get; }

    /// <summary>
    /// 
    /// </summary>
    public abstract HttpProtocol HttpProtocols { get; }

    /// <summary>
    /// 
    /// </summary>
    public sealed override TransportProtocol Protocol
    {
        get
        {
            return HttpProtocols switch
            {
                HttpProtocol.Http1 or HttpProtocol.Http2 and HttpProtocol.Http3 =>
                    throw new InvalidOperationException($"Dynamic transport layer is not supported. The {nameof(HttpConnectionTransport)} must return either a SingleStream of Multiplex."),
                HttpProtocol.Http1 or HttpProtocol.Http2 => TransportProtocol.Tcp,
                HttpProtocol.Http3 => TransportProtocol.Quic,
                _ => throw new InvalidOperationException($"Unsupported HTTP protocol: {HttpProtocols}")
            };
        }
    }
}