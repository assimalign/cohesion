using System;

using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.ForwardedHeaders.Internal;

namespace Assimalign.Cohesion.Web.ForwardedHeaders;

/// <summary>
/// Configures the forwarded-headers middleware that
/// <c>builder.Services.AddForwardedHeaders(forwarded =&gt; ...)</c> registers and
/// <see cref="WebApplicationExtensions.UseForwardedHeaders(IWebApplicationPipelineBuilder)"/> places.
/// </summary>
/// <remarks>
/// The trust model has no default: <see cref="ForwardedHeadersOptions.Headers"/> must select the
/// headers the trusted proxy manages. <see cref="Build"/> validates and snapshots the options, so a
/// misconfigured trust model fails where the application registers it, and changing the options
/// afterwards has no effect.
/// </remarks>
public sealed class ForwardedHeadersBuilder
{
    /// <summary>
    /// Initializes a builder whose options select no header, which <see cref="Build"/> refuses until
    /// the application chooses the headers its proxy manages.
    /// </summary>
    public ForwardedHeadersBuilder()
    {
    }

    /// <summary>
    /// Gets the trust model: the headers to honor, the known proxies and networks, the forward limit,
    /// and whether local transports are trusted.
    /// </summary>
    public ForwardedHeadersOptions Options { get; } = new();

    /// <summary>
    /// Builds the forwarded-headers middleware from <see cref="Options"/>.
    /// </summary>
    /// <returns>
    /// The middleware, which resolves the effective client address, scheme, and host and attaches them
    /// to the exchange as an <c>IHttpForwardedFeature</c>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <see cref="ForwardedHeadersOptions.Headers"/> is <see cref="ForwardedHeaderNames.None"/>, or a trust
    /// list contains a <see langword="null"/> entry.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="ForwardedHeadersOptions.ForwardLimit"/> is less than 1.
    /// </exception>
    public IWebApplicationMiddleware Build()
    {
        return new ForwardedHeadersMiddleware(new ForwardedHeadersResolver(Options));
    }
}
