using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The response of a transport exchange, on every HTTP version: a <c>200</c> with a header
/// collection seeded with the listener's optional Date and a buffered body until the application sets them.
/// </summary>
/// <remarks>
/// <para>
/// Only the owning <see cref="TransportHttpContext"/> constructs one, passing itself, so
/// <see cref="HttpContext"/> is assigned once, at construction, and is never observed unset.
/// </para>
/// <para>
/// <see cref="Trailers"/> is supported on HTTP/2 and HTTP/3, where the send path writes the staged
/// fields as a trailing HEADERS frame (RFC 9113 §8.1, RFC 9114 §4.1), and unsupported on HTTP/1.1
/// (decision 18) and for a CONNECT exchange, whose stream becomes a DATA-only tunnel (RFC 9113 §8.5,
/// RFC 9114 §4.4). The collection is created on first access, so a response that never touches it
/// allocates nothing for it.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("TransportHttpResponse")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class TransportHttpResponse : HttpResponse
{
    private readonly bool _supportsTrailers;
    private HttpTrailerCollection? _trailers;
    private HttpResponseHeaderPolicy _responseHeaderPolicy = HttpResponseHeaderPolicy.Default;
    private HttpResponseDateHeader? _dateHeader;

    internal void ConfigureDate(HttpResponseHeaderPolicy policy)
    {
        _responseHeaderPolicy = policy;
        _ = GetDateHeader();
    }

    internal HttpResponseDateHeader? GetDateHeader()
    {
        if ((int)StatusCode < 200)
        {
            return null;
        }
        if (!Headers.ContainsKey(HttpHeaderKey.Date))
        {
            _dateHeader = _responseHeaderPolicy.GetDateHeader();
            if (_dateHeader is not null)
            {
                Headers[HttpHeaderKey.Date] = _dateHeader.Value;
            }
        }
        return _dateHeader;
    }

    public TransportHttpResponse(TransportHttpContext context, bool supportsTrailers)
    {
        HttpContext = context;
        StatusCode = HttpStatusCode.Ok;
        Headers = new HttpHeaderCollection();
        Body = new MemoryStream();
        _supportsTrailers = supportsTrailers;
    }

    public override HttpStatusCode StatusCode { get; set; }

    public override HttpHeaderCollection Headers { get; }

    /// <summary>
    /// Gets the response trailer section. On HTTP/2 and HTTP/3 it is a supported collection whose
    /// fields go out after the body; adding a pseudo-header, a connection-specific field, or a field
    /// RFC 9110 §6.5.1 prohibits in trailers throws <see cref="System.ArgumentException"/>. On HTTP/1.1,
    /// and for a CONNECT exchange, it is <see cref="HttpTrailerCollection.Unsupported"/>.
    /// </summary>
    public override HttpTrailerCollection Trailers => _supportsTrailers
        ? _trailers ??= new HttpTrailerCollection(new TransportHttpTrailerFields(), isSupported: true)
        : HttpTrailerCollection.Unsupported;

    public override HttpContext HttpContext { get; }

    public override Stream Body { get; set; }

    /// <summary>
    /// The trailer fields the application staged, or <see langword="null"/> when it staged none — the
    /// collection was never touched, or is empty. The send path writes a trailing HEADERS frame only
    /// for a non-null value, so a response without trailers goes out exactly as it did before.
    /// </summary>
    internal HttpTrailerCollection? StagedTrailers => _trailers is { Count: > 0 } trailers ? trailers : null;

    private sealed class DebugView
    {
        private readonly TransportHttpResponse _target;

        public DebugView(TransportHttpResponse value)
        {
            _target = value;
        }

        public HttpStatusCode StatusCode => _target.StatusCode;
        public HttpHeaderCollection Headers => _target.Headers;
        public HttpContext HttpContext => _target.HttpContext;
        public Stream Body => _target.Body;
    }
}
