using System;
using System.Globalization;
using System.Text;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.HttpsPolicy.Internal;

namespace Assimalign.Cohesion.Web.HttpsPolicy;

/// <summary>
/// Configures the HTTP Strict Transport Security (RFC 6797) middleware that
/// <c>builder.Services.AddHsts(hsts =&gt; ...)</c> registers and
/// <see cref="HstsExtensions.UseHsts(IWebApplicationPipelineBuilder)"/> places.
/// </summary>
/// <remarks>
/// <see cref="Build"/> composes the <c>Strict-Transport-Security</c> value and compiles the excluded
/// hosts once, so an invalid option fails where the application registers HSTS, never per request.
/// </remarks>
public sealed class HstsBuilder
{
    /// <summary>
    /// Initializes a builder with the default policy: a 365-day <c>max-age</c>, neither
    /// <c>includeSubDomains</c> nor <c>preload</c>, and loopback hosts excluded.
    /// </summary>
    public HstsBuilder()
    {
    }

    /// <summary>
    /// Gets the policy's options: the <c>max-age</c>, the <c>includeSubDomains</c> and <c>preload</c>
    /// directives, and the excluded hosts.
    /// </summary>
    public HstsOptions Options { get; } = new();

    /// <summary>
    /// Builds the HSTS middleware from <see cref="Options"/>.
    /// </summary>
    /// <returns>The middleware, which emits the field on secure responses only (RFC 6797 §7.2).</returns>
    /// <exception cref="ArgumentException">
    /// The <c>max-age</c> is negative, or an excluded-host pattern is invalid (empty, port-bearing,
    /// malformed, or misusing the <c>*</c> wildcard).
    /// </exception>
    public IWebApplicationMiddleware Build()
    {
        if (Options.MaxAge < TimeSpan.Zero)
        {
            throw new ArgumentException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.HstsMaxAgeInvalid, Options.MaxAge),
                nameof(Options));
        }

        string headerValue = ComposeHeaderValue(Options);

        // No exclusions is the empty-allowlist case HttpHostMatcher.Create rejects; represent it as a null
        // matcher (emit on every secure host) rather than compiling one. A non-empty list is precompiled
        // once, so an invalid pattern throws here.
        HttpHostMatcher? excludedHosts = Options.ExcludedHosts.Count == 0
            ? null
            : HttpHostMatcher.Create(Options.ExcludedHosts);

        return new HstsMiddleware(headerValue, excludedHosts);
    }

    /// <summary>
    /// Composes the RFC 6797 field value <c>max-age=&lt;seconds&gt;[; includeSubDomains][; preload]</c>.
    /// The <c>max-age</c> is emitted as whole seconds.
    /// </summary>
    private static string ComposeHeaderValue(HstsOptions options)
    {
        long seconds = (long)options.MaxAge.TotalSeconds;

        StringBuilder builder = new();
        builder.Append("max-age=");
        builder.Append(seconds.ToString(CultureInfo.InvariantCulture));

        if (options.IncludeSubDomains)
        {
            builder.Append("; includeSubDomains");
        }

        if (options.Preload)
        {
            builder.Append("; preload");
        }

        return builder.ToString();
    }
}
