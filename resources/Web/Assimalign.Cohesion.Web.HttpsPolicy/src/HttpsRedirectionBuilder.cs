using System;
using System.Globalization;

using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.HttpsPolicy.Internal;

namespace Assimalign.Cohesion.Web.HttpsPolicy;

/// <summary>
/// Configures the HTTP-to-HTTPS redirection middleware that
/// <c>builder.Services.AddHttpsRedirection(redirection =&gt; ...)</c> registers and
/// <see cref="HttpsRedirectionExtensions.UseHttpsRedirection(IWebApplicationPipelineBuilder)"/> places.
/// </summary>
/// <remarks>
/// <see cref="Build"/> validates the status and port once, so an invalid option fails where the
/// application registers the redirection, never per request.
/// </remarks>
public sealed class HttpsRedirectionBuilder
{
    /// <summary>
    /// Initializes a builder with the defaults: a <c>307</c> redirect to port <c>443</c>.
    /// </summary>
    public HttpsRedirectionBuilder()
    {
    }

    /// <summary>
    /// Gets the redirection's options: the redirect status (<c>307</c> or <c>308</c>) and the HTTPS
    /// port.
    /// </summary>
    public HttpsRedirectionOptions Options { get; } = new();

    /// <summary>
    /// Builds the redirection middleware from <see cref="Options"/>.
    /// </summary>
    /// <returns>
    /// The middleware, which answers an insecure request with a method-preserving redirect to the same
    /// host and request target on the <c>https</c> scheme, and lets a secure request pass through.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The status is neither <c>307</c> nor <c>308</c>, or the HTTPS port is outside the range 1–65535.
    /// </exception>
    public IWebApplicationMiddleware Build()
    {
        int status = Options.StatusCode.Value;
        if (status is not (307 or 308))
        {
            throw new ArgumentException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.HttpsRedirectStatusInvalid, status),
                nameof(Options));
        }

        if (Options.HttpsPort is < 1 or > 65535)
        {
            throw new ArgumentException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.HttpsPortOutOfRange, Options.HttpsPort),
                nameof(Options));
        }

        return new HttpsRedirectionMiddleware(Options.StatusCode, Options.HttpsPort);
    }
}
