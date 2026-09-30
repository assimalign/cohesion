using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The response of a transport exchange, on every HTTP version: a <c>200</c> with an empty header
/// collection and a buffered body until the application sets them.
/// </summary>
/// <remarks>
/// Only the owning <see cref="TransportHttpContext"/> constructs one, passing itself, so
/// <see cref="HttpContext"/> is assigned once, at construction, and is never observed unset.
/// </remarks>
internal sealed class TransportHttpResponse : HttpResponse
{
    public TransportHttpResponse(TransportHttpContext context)
    {
        HttpContext = context;
        StatusCode = HttpStatusCode.Ok;
        Headers = new HttpHeaderCollection();
        Body = new MemoryStream();
    }

    public override HttpStatusCode StatusCode { get; set; }

    public override HttpHeaderCollection Headers { get; }

    public override HttpContext HttpContext { get; }

    public override Stream Body { get; set; }
}
