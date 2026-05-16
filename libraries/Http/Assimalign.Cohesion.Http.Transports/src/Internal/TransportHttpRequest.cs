using System;
using System.IO;

namespace Assimalign.Cohesion.Http.Transports.Internal;

internal abstract class TransportHttpRequest : HttpRequest
{
    private HttpContext? _httpContext;

    protected TransportHttpRequest(
        HttpHost host,
        HttpPath path,
        HttpMethod method,
        HttpScheme scheme,
        HttpQueryCollection query,
        HttpHeaderCollection headers,
        HttpCookieCollection cookies,
        Stream body)
    {
        Host = host;
        Path = path;
        Method = method;
        Scheme = scheme;
        Query = query;
        Headers = headers;
        Cookies = cookies;
        Body = body;
    }

    public override HttpHost Host { get; set; }

    public override HttpPath Path { get; set; }

    public override HttpMethod Method { get; set; }

    public override HttpScheme Scheme { get; set; }

    public override HttpQueryCollection Query { get; }

    public override HttpHeaderCollection Headers { get; }

    public override HttpCookieCollection Cookies { get; }

    public override HttpContext HttpContext => _httpContext
        ?? throw new InvalidOperationException(
            "The HttpContext back-reference has not been attached. " +
            "TransportHttpContext attaches the back-reference as the last step of its construction; " +
            "if you see this exception the request was used before that wire-up completed.");

    public override Stream Body { get; set; }

    /// <summary>
    /// Wires the owning <see cref="HttpContext"/> as the back-reference for this request.
    /// Called from <see cref="TransportHttpContext"/>'s constructor after the request has been
    /// assigned to <see cref="HttpContext.Request"/>. Idempotent within a single exchange &#8211;
    /// re-attaching the same context is a no-op; attaching a different one is rejected because
    /// the request-to-context relationship is fixed for the request's lifetime.
    /// </summary>
    internal void AttachContext(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_httpContext is null)
        {
            _httpContext = context;
            return;
        }

        if (!ReferenceEquals(_httpContext, context))
        {
            throw new InvalidOperationException(
                "The request is already attached to a different HttpContext. " +
                "A TransportHttpRequest belongs to a single exchange and cannot be re-parented.");
        }
    }
}
