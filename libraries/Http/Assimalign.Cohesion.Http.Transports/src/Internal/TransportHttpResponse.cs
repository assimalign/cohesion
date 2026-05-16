using System.IO;

namespace Assimalign.Cohesion.Http.Transports.Internal;

internal abstract class TransportHttpResponse : HttpResponse
{
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

    public override Stream Body { get; set; }
}
