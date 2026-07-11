using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Query.Internal;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

/// <summary>
/// Enforces the RFC 10008 &#167; 2.1 / &#167; 2.3 QUERY request-content rules: query content must
/// declare a parseable <c>Content-Type</c> (else 400/415 per policy), the declared type must fall
/// within the resource's advertised <c>Accept-Query</c> set (else 415), and — when the resource
/// declares its producible representations — the request's <c>Accept</c> field must be
/// satisfiable (else 406, RFC 9110 &#167; 12.5.1). Requests with any other method pass through
/// untouched.
/// </summary>
/// <remarks>
/// The middleware holds a builder-time snapshot of <see cref="WebQueryValidationOptions"/>; a
/// rejection sets the status imperatively and short-circuits without reading the request body
/// (the transport is responsible for the unread remainder of the exchange).
/// </remarks>
internal sealed class WebQueryValidationMiddleware : IWebApplicationMiddleware
{
    private readonly HttpAcceptQuery _acceptQuery;
    private readonly string? _acceptQueryField;
    private readonly HttpMediaType[] _responseMediaTypes;
    private readonly HttpStatusCode _invalidContentTypeStatusCode;

    public WebQueryValidationMiddleware(WebQueryValidationOptions options)
    {
        _acceptQuery = options.AcceptedMediaTypes.Count == 0
            ? HttpAcceptQuery.Empty
            : new HttpAcceptQuery(options.AcceptedMediaTypes);
        _acceptQueryField = options.AdvertiseAcceptQuery && !_acceptQuery.IsEmpty
            ? _acceptQuery.Serialize()
            : null;
        _responseMediaTypes = [.. options.SupportedResponseMediaTypes];
        _invalidContentTypeStatusCode = options.InvalidContentTypeStatusCode;
    }

    public async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        if (context.Request.Method != HttpMethod.Query)
        {
            await next.Invoke(context).ConfigureAwait(false);
            return;
        }

        IHttpResponse response = context.Response;

        // RFC 10008 §3 — advertise the accepted query formats up front so every response from
        // this resource (rejections included) signals QUERY support; the application can still
        // override the field before the head commits.
        if (_acceptQueryField is not null)
        {
            response.Headers[HttpHeaderKey.AcceptQuery] = _acceptQueryField;
        }

        // §2.1/§2.3 — the declared Content-Type is validated whenever present (a malformed
        // declaration is defective with or without content); when absent, content detected from
        // the message's length metadata makes the omission a MUST violation.
        if (context.Request.Headers.TryGetValue(HttpHeaderKey.ContentType, out HttpHeaderValue contentTypeField))
        {
            if (!HttpMediaType.TryParse(contentTypeField.Value, out HttpMediaType contentType))
            {
                response.StatusCode = _invalidContentTypeStatusCode;
                return;
            }

            if (!_acceptQuery.IsEmpty && !_acceptQuery.Accepts(contentType))
            {
                response.StatusCode = HttpStatusCode.UnsupportedMediaType;
                return;
            }
        }
        else if (HasDeclaredContent(context.Request))
        {
            response.StatusCode = _invalidContentTypeStatusCode;
            return;
        }

        // §2.1 — negotiate the response representation when the resource declares what it can
        // produce; a missing Accept accepts everything (RFC 9110 §12.5.1).
        if (_responseMediaTypes.Length > 0
            && !HttpContentNegotiation.TryNegotiateMediaType(
                context.Request.Headers.GetValue(HttpHeaderKey.Accept),
                _responseMediaTypes,
                out _))
        {
            response.StatusCode = HttpStatusCode.NotAcceptable;
            return;
        }

        await next.Invoke(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Detects request content from the message metadata without consuming the body:
    /// <c>Content-Length</c> when declared, then <c>Transfer-Encoding</c> (HTTP/1.1 chunked), then
    /// a buffered body's observable length. An HTTP/2+ request that streams content with no
    /// <c>Content-Length</c> is not detectable at head time without reading — such a request
    /// passes through here, and its (conformant) <c>Content-Type</c> was already validated above.
    /// </summary>
    private static bool HasDeclaredContent(IHttpRequest request)
    {
        if (request.Headers.TryGetValue(HttpHeaderKey.ContentLength, out HttpHeaderValue contentLength))
        {
            // An unparseable Content-Length is the transport's problem (it rejects ambiguous
            // framing before dispatch); treat a parseable declaration authoritatively.
            return !long.TryParse(contentLength.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long length)
                || length > 0;
        }

        if (request.Headers.ContainsKey(HttpHeaderKey.TransferEncoding))
        {
            return true;
        }

        // No length metadata: HTTP/1.1 has no body by definition (RFC 9112 §6); a buffered
        // HTTP/2+ body still betrays its length through the seekable stream.
        Stream body = request.Body;
        return body.CanSeek && body.Length > 0;
    }
}
