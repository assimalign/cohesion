using System.IO;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Compression.Internal;

/// <summary>
/// A pass-through <see cref="IHttpRequest"/> whose <see cref="Body"/> is the decompressed,
/// size-guarded stream. Every other member forwards to the real request. Used because
/// <see cref="IHttpRequest.Body"/> is get-only and cannot be swapped in place, so transparent
/// decompression is delivered by decorating the request the pipeline sees.
/// </summary>
internal sealed class RequestDecompressionHttpRequest : IHttpRequest
{
    private readonly IHttpRequest _inner;
    private readonly Stream _body;
    private readonly IHttpContext _context;

    public RequestDecompressionHttpRequest(IHttpRequest inner, Stream body, IHttpContext context)
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
