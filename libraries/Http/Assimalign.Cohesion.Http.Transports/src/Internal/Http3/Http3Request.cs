using System.IO;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http3;

internal sealed class Http3Request : TransportHttpRequest
{
    public Http3Request(
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
