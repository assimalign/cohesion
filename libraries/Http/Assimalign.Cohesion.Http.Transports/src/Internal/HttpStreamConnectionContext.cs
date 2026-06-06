using System;
using System.IO;
using System.Net;
using System.Collections.Generic;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Internal;

internal abstract class HttpStreamConnectionContext : HttpConnectionContext
{

    protected HttpStreamConnectionContext(ITransportConnectionContext transportContext, bool isSecure)
    {
        TransportContext = transportContext;
        Stream = transportContext.Pipe.Stream;
        ConnectionInfo = new HttpConnectionInfo(transportContext.LocalEndPoint, transportContext.RemoteEndPoint);
        IsSecure = isSecure;
    }

    protected bool IsSecure { get; }
    protected ITransportConnectionContext TransportContext { get; }
    protected Stream Stream { get; }
    protected HttpConnectionInfo ConnectionInfo { get; }
    public override EndPoint LocalEndPoint => TransportContext.LocalEndPoint;
    public override EndPoint RemoteEndPoint => TransportContext.RemoteEndPoint;
    public override ITransportConnectionPipe Pipe => TransportContext.Pipe;
    public override IDictionary<string, object?> Items => TransportContext.Items;
    protected HttpScheme GetScheme()
    {
        return IsSecure ? HttpScheme.Https : HttpScheme.Http;
    }
}
