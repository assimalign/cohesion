using System.IO;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// A pass-through <see cref="IHttpRequest"/> whose <see cref="Path"/> and <see cref="Query"/> are the
/// rewritten values. Every other member forwards to the request it wraps, which may itself be another view
/// (a decompressed body, for example), and <see cref="HttpContext"/> is the rewrite view, so a component that
/// reaches the exchange through the request stays on the rewritten URL.
/// </summary>
internal sealed class RewriteHttpRequest : IHttpRequest
{
    private readonly IHttpRequest _inner;
    private readonly HttpPath _path;
    private readonly IHttpQueryCollection _query;
    private readonly IHttpContext _context;

    public RewriteHttpRequest(IHttpRequest inner, HttpPath path, IHttpQueryCollection query, IHttpContext context)
    {
        _inner = inner;
        _path = path;
        _query = query;
        _context = context;
    }

    public HttpHost Host => _inner.Host;

    public HttpPath Path => _path;

    public HttpMethod Method => _inner.Method;

    public HttpScheme Scheme => _inner.Scheme;

    public IHttpQueryCollection Query => _query;

    public IHttpHeaderCollection Headers => _inner.Headers;

    public IHttpTrailerCollection Trailers => _inner.Trailers;

    public IHttpContext HttpContext => _context;

    public Stream Body => _inner.Body;
}
