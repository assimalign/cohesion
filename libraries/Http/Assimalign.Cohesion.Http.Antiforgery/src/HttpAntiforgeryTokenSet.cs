using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Http;


/// <summary>
/// The antiforgery token pair (cookie and request token) for a request.
/// </summary>
public class HttpAntiforgeryTokenSet
{
    /// <summary>
    /// Creates the antiforgery token pair (cookie and request token) for a request.
    /// </summary>
    /// <param name="requestToken">The token that is supplied in the request.</param>
    /// <param name="cookieToken">The token that is supplied in the request cookie.</param>
    /// <param name="formFieldName">The name of the form field used for the request token.</param>
    /// <param name="headerName">The name of the header used for the request token.</param>
    public HttpAntiforgeryTokenSet(
        string? requestToken,
        string? cookieToken,
        string formFieldName,
        string? headerName)
    {
        ArgumentNullException.ThrowIfNull(formFieldName);

        RequestToken = requestToken;
        CookieToken = cookieToken;
        FormFieldName = formFieldName;
        HeaderName = headerName;
    }

    /// <summary>
    /// Gets the request token.
    /// </summary>
    public string? RequestToken { get; }

    /// <summary>
    /// Gets the name of the form field used for the request token.
    /// </summary>
    public string FormFieldName { get; }

    /// <summary>
    /// Gets the name of the header used for the request token.
    /// </summary>
    public string? HeaderName { get; }

    /// <summary>
    /// Gets the cookie token.
    /// </summary>
    public string? CookieToken { get; }
}
