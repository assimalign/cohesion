using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal;

internal sealed class Http1Request : TransportHttpRequest
{
    public Http1Request(
        HttpHost host,
        HttpPath path,
        HttpMethod method,
        HttpScheme scheme,
        HttpQueryCollection query,
        HttpHeaderCollection headers,
        Stream body,
        HttpTrailerCollection? trailers = null)
        : base(host, path, method, scheme, query, headers, body, trailers)
    {
    }
}
