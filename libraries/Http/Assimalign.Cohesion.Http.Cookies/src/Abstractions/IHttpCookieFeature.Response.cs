namespace Assimalign.Cohesion.Http;

/// <summary>
/// Per-exchange response-cookie state stored in <see cref="IHttpContext.Features"/>.
/// </summary>
/// <remarks>
/// <para>
/// The protocol core deliberately omits a <c>Cookies</c> property on
/// <see cref="IHttpResponse"/> &#8211; the cookie collection is a typed
/// convenience that the transport layer drains into <c>Set-Cookie</c>
/// headers at response-flush time. The
/// <c>Assimalign.Cohesion.Http.Cookies</c> package layers response-cookie
/// state on top of the protocol core by attaching this feature to
/// <see cref="IHttpContext.Features"/>. Consumers prefer the
/// <see cref="HttpResponseCookieExtensions.Cookies"/> extension property
/// on <see cref="IHttpResponse"/>; middleware that needs a richer feature
/// implementation (signed cookies, encrypted cookies, custom serialization)
/// can install one directly via
/// <c>context.Features.Set&lt;IHttpResponseCookieFeature&gt;(...)</c>.
/// </para>
/// </remarks>
public interface IHttpResponseCookieFeature : IHttpFeature
{
    /// <summary>
    /// Gets the mutable collection of cookies to be emitted as
    /// <c>Set-Cookie</c> headers on the response. Never <see langword="null"/>.
    /// </summary>
    IHttpCookieCollection Cookies { get; }
}
