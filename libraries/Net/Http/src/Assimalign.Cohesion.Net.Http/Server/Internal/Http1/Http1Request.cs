using System;
using System.IO;
using System.Security.Claims;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal class Http1Request : IHttpRequest
{
    public HttpPath Path { get; set; }
    public HttpMethod Method { get; set; }
    public HttpScheme Scheme  { get; set; }

    public HttpQueryCollection Query { get; } = new();
    IHttpQueryCollection IHttpRequest.Query => this.Query;

    public HttpHeaderCollection Headers { get; } = new();
    IHttpHeaderCollection IHttpRequest.Headers => this.Headers;

    public IHttpCookieCollection Cookies => throw new NotImplementedException();
    public Stream Body { get; set; }

    public HttpHost Host => throw new NotImplementedException();

    public IHttpFormCollection Form => throw new NotImplementedException();

    public ClaimsPrincipal ClaimsPrincipal => throw new NotImplementedException();
}
