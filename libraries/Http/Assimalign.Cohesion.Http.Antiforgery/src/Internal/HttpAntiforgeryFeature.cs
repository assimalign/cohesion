using System;

namespace Assimalign.Cohesion.Http.Internal;

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
    /// <remarks>
    /// The slot is named for the contract (see <see cref="IHttpAntiforgeryFeature"/>), so assigning a
    /// service replaces one an application registered instead of adding a second feature.
    /// </remarks>
    public string Name => nameof(IHttpAntiforgeryFeature);

    /// <inheritdoc />
    public IHttpAntiforgery Antiforgery { get; }
}
