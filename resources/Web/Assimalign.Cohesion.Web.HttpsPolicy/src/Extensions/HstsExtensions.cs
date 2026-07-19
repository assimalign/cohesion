using System;
using System.Globalization;
using System.Text;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.HttpsPolicy.Internal;

namespace Assimalign.Cohesion.Web.HttpsPolicy;

/// <summary>
/// Pipeline-builder extension that wires HTTP Strict Transport Security (RFC 6797) emission into the
/// Web application middleware pipeline.
/// </summary>
public static class HstsExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds middleware that emits the <c>Strict-Transport-Security</c> field — composed once from
        /// the supplied options — on secure responses only (RFC 6797 §7.2), skipping the excluded hosts
        /// (loopback by default).
        /// </summary>
        /// <param name="configure">
        /// An optional callback to configure the <c>max-age</c>, the <c>includeSubDomains</c> and
        /// <c>preload</c> directives, and the excluded hosts. When <see langword="null"/>, the defaults
        /// apply (365-day <c>max-age</c>, both directives off, loopback excluded).
        /// </param>
        /// <returns>The same <see cref="IWebApplicationPipelineBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// The configured <c>max-age</c> is negative, or an excluded-host pattern is invalid (empty,
        /// port-bearing, malformed, or misusing the <c>*</c> wildcard).
        /// </exception>
        /// <remarks>
        /// <para>
        /// Register this <em>before the exception boundary</em> (and generally near the front of the
        /// pipeline): the field is applied after the pipeline unwinds, so placing the boundary inside
        /// this middleware means a reset error response served over TLS still carries the policy. The
        /// header value and the excluded-host matcher are built here, at builder time; the middleware
        /// resolves nothing per request.
        /// </para>
        /// <para>
        /// Connection security is the transport-derived typed scheme (#763); there is no scheme-string
        /// sniffing. Excluded-host matching reuses <see cref="HttpHostMatcher"/> from the Http core, so
        /// it shares the same case-insensitive, port-ignoring, IPv6-bracket-insensitive host semantics
        /// as the rest of the stack.
        /// </para>
        /// </remarks>
        public IWebApplicationPipelineBuilder UseHsts(Action<HstsOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            HstsOptions options = new();
            configure?.Invoke(options);

            if (options.MaxAge < TimeSpan.Zero)
            {
                throw new ArgumentException(
                    $"The HSTS max-age must not be negative; got {options.MaxAge}.",
                    nameof(configure));
            }

            string headerValue = ComposeHeaderValue(options);

            // No exclusions is the empty-allowlist case HttpHostMatcher.Create rejects; represent it as
            // a null matcher (emit on every secure host) rather than compiling one. A non-empty list is
            // precompiled once — an invalid pattern throws here, at builder time.
            HttpHostMatcher? excludedHosts = options.ExcludedHosts.Count == 0
                ? null
                : HttpHostMatcher.Create(options.ExcludedHosts);

            return builder.Use(new HstsMiddleware(headerValue, excludedHosts));
        }
    }

    /// <summary>
    /// Composes the RFC 6797 field value <c>max-age=&lt;seconds&gt;[; includeSubDomains][; preload]</c>
    /// once from the captured options. The <c>max-age</c> is emitted as whole seconds.
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
