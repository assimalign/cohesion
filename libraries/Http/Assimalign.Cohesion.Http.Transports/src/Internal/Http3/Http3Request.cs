using System.IO;
using System.Security.Claims;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http3;

internal sealed class Http3Request : TransportHttpRequest
{
    public Http3Request(
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
