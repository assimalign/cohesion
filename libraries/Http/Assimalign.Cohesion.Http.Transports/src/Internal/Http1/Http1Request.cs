using System.IO;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http1;

internal sealed class Http1Request : TransportHttpRequest
{
    public Http1Request(
        HttpHost host,
        HttpPath path,
        HttpMethod method,
        HttpScheme scheme,
        HttpQueryCollection query,
        HttpHeaderCollection headers,
        Stream body)
        : base(host, path, method, scheme, query, headers, body)
    {
    }
}
