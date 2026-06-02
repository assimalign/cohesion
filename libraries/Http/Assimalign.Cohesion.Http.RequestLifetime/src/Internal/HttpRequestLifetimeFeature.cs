using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Default <see cref="IHttpRequestLifetimeFeature"/> implementation installed
/// by <see cref="HttpContextRequestLifetimeExtensions"/> to carry the
/// <see cref="IHttpRequestLifetime"/> on an exchange.
/// </summary>
internal sealed class HttpRequestLifetimeFeature : IHttpRequestLifetimeFeature
{
    public HttpRequestLifetimeFeature(IHttpRequestLifetime requestLifetime)
    {
        ArgumentNullException.ThrowIfNull(requestLifetime);
        RequestLifetime = requestLifetime;
    }

    /// <inheritdoc />
    public string Name => nameof(HttpRequestLifetimeFeature);

    /// <inheritdoc />
    public IHttpRequestLifetime RequestLifetime { get; }
}
