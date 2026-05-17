namespace Assimalign.Cohesion.Http;

/// <summary>
/// Default <see cref="IHttpResponseCookieFeature"/> implementation. Holds a
/// fresh <see cref="HttpCookieCollection"/> that callers mutate via
/// <see cref="HttpResponseCookieExtensions.Cookies"/>; the transport layer
/// drains the collection into <c>Set-Cookie</c> headers at response-flush
/// time.
/// </summary>
internal sealed class HttpResponseCookieFeature : IHttpResponseCookieFeature
{
    public HttpResponseCookieFeature(IHttpHeaderCollection headers)
    {
        Cookies = new HttpCookieCollection(headers);
    }

    public string Name => nameof(IHttpResponseCookieFeature);
    public IHttpCookieCollection Cookies { get; }

}
