using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Default <see cref="IHttpAntiforgeryFeature"/> implementation installed by
/// <see cref="HttpContextAntiforgeryExtensions"/> to carry the resolved
/// <see cref="IHttpAntiforgery"/> service on an exchange.
/// </summary>
internal sealed class HttpAntiforgeryFeature : IHttpAntiforgeryFeature
{
    public HttpAntiforgeryFeature(IHttpAntiforgery antiforgery)
    {
        ArgumentNullException.ThrowIfNull(antiforgery);
        Antiforgery = antiforgery;
    }

    /// <inheritdoc />
    public string Name => nameof(HttpAntiforgeryFeature);

    /// <inheritdoc />
    public IHttpAntiforgery Antiforgery { get; }
}
