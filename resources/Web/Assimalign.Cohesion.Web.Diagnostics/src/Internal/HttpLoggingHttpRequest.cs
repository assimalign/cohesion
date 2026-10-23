using System.IO;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Diagnostics.Internal;

/// <summary>
/// Observes reads from any request implementation, including upstream middleware request views.
/// The request's context points back to the logging view so reads through it are observed too.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("HttpLoggingHttpRequest")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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

    private sealed class DebugView
    {
        private readonly HttpLoggingHttpRequest _target;

        public DebugView(HttpLoggingHttpRequest value)
        {
            _target = value;
        }

        public HttpHost Host => _target.Host;
        public HttpPath Path => _target.Path;
        public HttpMethod Method => _target.Method;
        public HttpScheme Scheme => _target.Scheme;
        public IHttpQueryCollection Query => _target.Query;
        public IHttpHeaderCollection Headers => _target.Headers;
        public IHttpTrailerCollection Trailers => _target.Trailers;
        public IHttpContext HttpContext => _target.HttpContext;
        public Stream Body => _target.Body;
    }
}
