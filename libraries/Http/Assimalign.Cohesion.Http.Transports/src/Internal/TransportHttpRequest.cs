using System.IO;

namespace Assimalign.Cohesion.Http.Transports.Internal;

internal abstract class TransportHttpRequest : HttpRequest
{
    protected TransportHttpRequest(
        HttpHost host,
        HttpPath path,
        HttpMethod method,
        HttpScheme scheme,
        HttpQueryCollection query,
        HttpHeaderCollection headers,
        HttpCookieCollection cookies,
        Stream body)
    {
        Host = host;
        Path = path;
        Method = method;
        Scheme = scheme;
        Query = query;
        Headers = headers;
        Cookies = cookies;
        Body = body;
    }

    public override HttpHost Host { get; set; }

    public override HttpPath Path { get; set; }

    public override HttpMethod Method { get; set; }

    public override HttpScheme Scheme { get; set; }

    public override HttpQueryCollection Query { get; }

    public override HttpHeaderCollection Headers { get; }

    public override HttpCookieCollection Cookies { get; }

    public override Stream Body { get; set; }
}
