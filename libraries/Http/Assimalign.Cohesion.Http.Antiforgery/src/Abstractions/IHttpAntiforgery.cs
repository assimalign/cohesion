using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

public interface IHttpAntiforgery
{
    /// <summary>
    /// Generates an <see cref="HttpAntiforgeryTokenSet"/> for this request and stores the cookie token
    /// in the response. This operation also sets the "Cache-control" and "Pragma" headers to "no-cache" and
    /// the "X-Frame-Options" header to "SAMEORIGIN".
    /// </summary>
    /// <param name="httpContext">The <see cref="HttpContext"/> associated with the current request.</param>
    /// <returns>An <see cref="HttpAntiforgeryTokenSet" /> with tokens for the response.</returns>
    /// <remarks>
    /// This method has a side effect:
    /// A response cookie is set if there is no valid cookie associated with the request.
    /// </remarks>
    HttpAntiforgeryTokenSet GetAndStoreTokens(IHttpContext httpContext);

    /// <summary>
    /// Generates an <see cref="HttpAntiforgeryTokenSet"/> for this request.
    /// </summary>
    /// <param name="httpContext">The <see cref="IHttpContext"/> associated with the current request.</param>
    /// <returns>The <see cref="AntiforgeryTokenSet"/> for this request.</returns>
    /// <remarks>
    /// Unlike <see cref="GetAndStoreTokens(IHttpContext)"/>, this method has no side effect. The caller
    /// is responsible for setting the response cookie and injecting the returned
    /// form token as appropriate.
    /// </remarks>
    HttpAntiforgeryTokenSet GetTokens(IHttpContext httpContext);

    /// <summary>
    /// Asynchronously returns a value indicating whether the request passes antiforgery validation. If the
    /// request uses a safe HTTP method (GET, HEAD, OPTIONS, TRACE), the antiforgery token is not validated.
    /// </summary>
    /// <param name="httpContext">The <see cref="IHttpContext"/> associated with the current request.</param>
    /// <returns>
    /// A <see cref="Task{Boolean}"/> that, when completed, returns <c>true</c> if the request uses a safe HTTP
    /// method or contains a valid antiforgery token, otherwise returns <c>false</c>.
    /// </returns>
    Task<bool> IsRequestValidAsync(IHttpContext httpContext);

    /// <summary>
    /// Validates an antiforgery token that was supplied as part of the request.
    /// </summary>
    /// <param name="httpContext">The <see cref="IHttpContext"/> associated with the current request.</param>
    /// <returns>A <see cref="Task"/> that completes when validation has completed.</returns>
    /// <exception cref="AntiforgeryValidationException">
    /// Thrown when the request does not include a valid antiforgery token.
    /// </exception>
    Task ValidateRequestAsync(IHttpContext httpContext);

    /// <summary>
    /// Generates and stores an antiforgery cookie token if one is not available or not valid.
    /// </summary>
    /// <param name="httpContext">The <see cref="IHttpContext"/> associated with the current request.</param>
    void SetCookieTokenAndHeader(IHttpContext httpContext);
}
