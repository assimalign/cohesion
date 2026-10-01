namespace Assimalign.Cohesion.Http;

/// <summary>
/// Per-exchange response-cookie state stored in <see cref="IHttpContext.Features"/>.
/// </summary>
/// <remarks>
/// <para>
/// The protocol core deliberately omits a <c>Cookies</c> property on
/// <see cref="IHttpResponse"/> &#8211; the cookie collection is a typed
/// convenience that the <c>Assimalign.Cohesion.Http.Cookies</c> package
/// layers on top of the protocol core by attaching this feature to
/// <see cref="IHttpContext.Features"/>. The collection writes every change
/// through to the response's <c>Set-Cookie</c> header, one value per cookie,
/// and the transports serialize that header like any other field. Consumers
/// prefer the <see cref="HttpResponseCookieExtensions.Cookies"/> extension
/// property on <see cref="IHttpResponse"/>.
/// </para>
/// <para>
/// A replacement feature (signed cookies, encrypted cookies, a cookie policy)
/// must keep that header in sync, typically by queuing into the collection of
/// the feature it replaces. The feature collection is keyed by name, so remove
/// the existing feature by its <see cref="IHttpFeature.Name"/> before calling
/// <c>context.Features.Set&lt;IHttpResponseCookieFeature&gt;(...)</c>;
/// otherwise both features stay installed and type lookups resolve the first.
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
