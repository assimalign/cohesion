namespace Assimalign.Cohesion.Http;

/// <summary>
/// Per-exchange request-cookie state stored in <see cref="IHttpContext.Features"/>.
/// </summary>
/// <remarks>
/// <para>
/// The protocol core deliberately omits a <c>Cookies</c> property on
/// <see cref="IHttpRequest"/> &#8211; the cookie collection is a parsing
/// convenience built on top of the wire-level <c>Cookie</c> header, not a
/// member of the request contract itself. The
/// <c>Assimalign.Cohesion.Http.Cookies</c> package layers cookie state on top
/// of the protocol core by attaching this feature to
/// <see cref="IHttpContext.Features"/>. Consumers prefer the
/// <see cref="HttpCookieExtensions.Cookies"/> extension property on
/// <see cref="IHttpRequest"/>; middleware that needs a richer feature
/// implementation (alternate parsing rules, signed cookies, etc.) can install
/// one directly via
/// <c>context.Features.Set&lt;IHttpRequestCookieFeature&gt;(...)</c>.
/// </para>
/// </remarks>
public interface IHttpRequestCookieFeature : IHttpFeature
{
    /// <summary>
    /// Gets the cookies parsed from the request's <c>Cookie</c> header(s).
    /// Never <see langword="null"/>; an absent or empty header yields an
    /// empty collection.
    /// </summary>
    IHttpCookieCollection Cookies { get; }
}
