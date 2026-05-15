using System.IO;
using System.Security.Claims;

namespace Assimalign.Cohesion.Http.Transports.Internal;

internal abstract class TransportHttpRequest : HttpRequest
{
    protected TransportHttpRequest(
        HttpHost host,
        HttpPath path,
        HttpMethod method,
        HttpScheme scheme,
        IHttpQueryCollection query,
        IHttpHeaderCollection headers,
        IHttpCookieCollection cookies,
        Stream body,
        ClaimsPrincipal claimsPrincipal)
    {
        Host = host;
        Path = path;
        Method = method;
        Scheme = scheme;
        Query = query;
        Headers = headers;
        Cookies = cookies;
        Body = body;
        ClaimsPrincipal = claimsPrincipal;
    }

    public override HttpHost Host { get; set; }

    public override HttpPath Path { get; set; }

    public override HttpMethod Method { get; set; }

    public override HttpScheme Scheme { get; set; }

    public override IHttpQueryCollection Query { get; }

    public override IHttpHeaderCollection Headers { get; }

    public override IHttpCookieCollection Cookies { get; }

    public override Stream Body { get; set; }

    public override ClaimsPrincipal ClaimsPrincipal { get; set; }
}
