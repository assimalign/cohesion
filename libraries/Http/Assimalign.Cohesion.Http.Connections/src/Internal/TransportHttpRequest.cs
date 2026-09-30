using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The request of a transport exchange, on every HTTP version.
/// </summary>
/// <remarks>
/// Only the owning <see cref="TransportHttpContext"/> constructs one. It passes itself and the
/// decoded <see cref="TransportHttpRequestHead"/>, so <see cref="HttpContext"/> is assigned once, at
/// construction, and is never observed unset.
/// </remarks>
internal sealed class TransportHttpRequest : HttpRequest
{
    public TransportHttpRequest(TransportHttpContext context, in TransportHttpRequestHead head)
    {
        HttpContext = context;
        Host = head.Host;
        Path = head.Path;
        Method = head.Method;
        Scheme = head.Scheme;
        Query = head.Query;
        Headers = head.Headers;
        Body = head.Body;
        Trailers = head.Trailers ?? HttpTrailerCollection.Unsupported;
    }

    public override HttpHost Host { get; set; }
    public override HttpPath Path { get; set; }
    public override HttpMethod Method { get; set; }
    public override HttpScheme Scheme { get; set; }
    public override HttpQueryCollection Query { get; }
    public override HttpHeaderCollection Headers { get; }
    public override HttpTrailerCollection Trailers { get; }
    public override HttpContext HttpContext { get; }
    public override Stream Body { get; set; }
}
