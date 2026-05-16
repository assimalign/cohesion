using System;
using System.IO;

namespace Assimalign.Cohesion.Http.Transports.Internal;

internal abstract class TransportHttpResponse : HttpResponse
{
    private HttpContext? _httpContext;

    protected TransportHttpResponse()
    {
        StatusCode = HttpStatusCode.Ok;
        Headers = new HttpHeaderCollection();
        Cookies = new HttpCookieCollection();
        Body = new MemoryStream();
    }

    public override HttpStatusCode StatusCode { get; set; }

    public override HttpHeaderCollection Headers { get; }

    public override HttpCookieCollection Cookies { get; }

    public override HttpContext HttpContext => _httpContext
        ?? throw new InvalidOperationException(
            "The HttpContext back-reference has not been attached. " +
            "TransportHttpContext attaches the back-reference as the last step of its construction; " +
            "if you see this exception the response was used before that wire-up completed.");

    public override Stream Body { get; set; }

    /// <summary>
    /// Wires the owning <see cref="HttpContext"/> as the back-reference for this response.
    /// Called from <see cref="TransportHttpContext"/>'s constructor after the response has been
    /// assigned to <see cref="HttpContext.Response"/>. Idempotent within a single exchange &#8211;
    /// re-attaching the same context is a no-op; attaching a different one is rejected because
    /// the response-to-context relationship is fixed for the response's lifetime.
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
                "The response is already attached to a different HttpContext. " +
                "A TransportHttpResponse belongs to a single exchange and cannot be re-parented.");
        }
    }
}
