namespace Assimalign.Cohesion.Http;

/// <summary>
/// One of the four HTTP/1.1 request-target forms defined by RFC 9112 &#167; 3.2.
/// </summary>
/// <remarks>
/// The form a request-target uses depends on the HTTP method and the role of the
/// recipient (origin server, proxy, tunnel). Validation rules tying methods to forms
/// (e.g. <see cref="HttpMethod.Connect"/> requires <see cref="Authority"/>) are enforced
/// by <see cref="HttpRequestTarget.TryParse(System.ReadOnlySpan{char}, HttpMethod, out HttpRequestTarget, out string)"/>.
/// </remarks>
public enum HttpRequestTargetForm
{
    /// <summary>The request-target has not been parsed or is invalid.</summary>
    Unknown = 0,

    /// <summary>
    /// origin-form &#8211; <c>absolute-path [ "?" query ]</c>. The most common form,
    /// used for direct requests to an origin server.
    /// </summary>
    Origin = 1,

    /// <summary>
    /// absolute-form &#8211; <c>absolute-URI</c>. Used when sending a request to a proxy
    /// so the proxy knows which origin to forward to.
    /// </summary>
    Absolute = 2,

    /// <summary>
    /// authority-form &#8211; <c>uri-host ":" port</c>. Used only with
    /// <see cref="HttpMethod.Connect"/> to establish a tunnel.
    /// </summary>
    Authority = 3,

    /// <summary>
    /// asterisk-form &#8211; <c>"*"</c>. Used only with <see cref="HttpMethod.Options"/>
    /// to address the server itself rather than a specific resource.
    /// </summary>
    Asterisk = 4,
}
