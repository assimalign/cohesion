using System.IO;
using System.Security.Claims;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http2;

internal sealed class Http2Request : TransportHttpRequest
{
    public Http2Request(
        HttpHost host,
        HttpPath path,
        HttpMethod method,
        HttpScheme scheme,
        IHttpQueryCollection query,
        IHttpHeaderCollection headers,
        IHttpCookieCollection cookies,
        IHttpFormCollection form,
        Stream body,
        ClaimsPrincipal claimsPrincipal)
        : base(host, path, method, scheme, query, headers, cookies, form, body, claimsPrincipal)
    {
    }
}
