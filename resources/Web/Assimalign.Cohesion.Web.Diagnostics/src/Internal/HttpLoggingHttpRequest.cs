using System.IO;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Diagnostics.Internal;

/// <summary>
/// Observes reads from any request implementation, including upstream middleware request views.
/// The request's context points back to the logging view so reads through it are observed too.
/// </summary>
internal sealed class HttpLoggingHttpRequest : IHttpRequest
{
    private readonly IHttpRequest _inner;
    private readonly Stream _body;
    private readonly IHttpContext _context;

    public HttpLoggingHttpRequest(IHttpRequest inner, Stream body, IHttpContext context)
    {
        _inner = inner;
        _body = body;
        _context = context;
    }

    public HttpHost Host => _inner.Host;

    public HttpPath Path => _inner.Path;

    public HttpMethod Method => _inner.Method;

    public HttpScheme Scheme => _inner.Scheme;

    public IHttpQueryCollection Query => _inner.Query;

    public IHttpHeaderCollection Headers => _inner.Headers;

    public IHttpTrailerCollection Trailers => _inner.Trailers;

    public IHttpContext HttpContext => _context;

    public Stream Body => _body;
}
