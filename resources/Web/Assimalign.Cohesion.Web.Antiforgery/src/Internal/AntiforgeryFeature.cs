using System;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Antiforgery.Internal;

/// <summary>
/// The application-level <see cref="IHttpAntiforgeryFeature"/> that <c>AddAntiforgery</c> registers. The
/// host seeds it onto every exchange, so handlers mint tokens through <c>context.RequireAntiforgery</c>,
/// and <c>UseAntiforgery</c> resolves it from the application context when the pipeline is built.
/// </summary>
/// <remarks>
/// It carries the options the service was created from because the middleware reads the configured
/// token header name to decide whether a form body has to be read at all.
/// </remarks>
internal sealed class AntiforgeryFeature : IHttpAntiforgeryFeature
{
    public AntiforgeryFeature(IHttpAntiforgery antiforgery, HttpAntiforgeryOptions options)
    {
        ArgumentNullException.ThrowIfNull(antiforgery);
        ArgumentNullException.ThrowIfNull(options);

        Antiforgery = antiforgery;
        Options = options;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The slot is named for the contract, as Http.Antiforgery's own feature is, so assigning
    /// <c>context.Antiforgery</c> on an exchange replaces this feature instead of adding a second one.
    /// </remarks>
    public string Name => nameof(IHttpAntiforgeryFeature);

    /// <inheritdoc />
    public IHttpAntiforgery Antiforgery { get; }

    /// <summary>
    /// Gets the options <see cref="Antiforgery"/> was created from.
    /// </summary>
    public HttpAntiforgeryOptions Options { get; }
}
