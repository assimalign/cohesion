using System;
using System.Collections.Generic;
using System.IO;
using System.Net;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Internal;

internal abstract class HttpStreamConnectionContext : HttpConnectionContext
{
    protected HttpStreamConnectionContext(
        ITransportConnectionContext transportContext,
        bool isSecure,
        Func<IHttpFeatureCollection>? createFeatures)
    {
        TransportContext = transportContext;
        Stream = transportContext.Pipe.GetStream();
        ConnectionInfo = new HttpConnectionInfo(transportContext.LocalEndPoint, transportContext.RemoteEndPoint, isSecure);
        CreateFeatures = createFeatures;
    }

    protected ITransportConnectionContext TransportContext { get; }

    protected Stream Stream { get; }

    protected HttpConnectionInfo ConnectionInfo { get; }

    /// <summary>
    /// Factory invoked once per <see cref="IHttpContext"/> on this
    /// connection to produce the per-request
    /// <see cref="IHttpFeatureCollection"/>. <see langword="null"/> when
    /// the listener was not configured with
    /// <see cref="HttpConnectionListenerOptions.CreateFeatures"/>; the
    /// per-request constructor then defaults to a fresh empty collection.
    /// </summary>
    protected Func<IHttpFeatureCollection>? CreateFeatures { get; }

    public override EndPoint LocalEndPoint => TransportContext.LocalEndPoint;

    public override EndPoint RemoteEndPoint => TransportContext.RemoteEndPoint;

    public override ITransportConnectionPipe Pipe => TransportContext.Pipe;

    public override IDictionary<string, object?> Items => TransportContext.Items;

    protected static HttpScheme GetScheme(bool isSecure)
    {
        return isSecure ? HttpScheme.Https : HttpScheme.Http;
    }
}
