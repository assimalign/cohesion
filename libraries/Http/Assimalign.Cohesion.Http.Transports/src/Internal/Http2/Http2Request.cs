using System.IO;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http2;

internal sealed class Http2Request : TransportHttpRequest
{
    public Http2Request(
        HttpHost host,
        HttpPath path,
        HttpMethod method,
        HttpScheme scheme,
        HttpQueryCollection query,
        HttpHeaderCollection headers,
        HttpCookieCollection cookies,
        Stream body)
        : base(host, path, method, scheme, query, headers, cookies, body)
    {
    }
}
