using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Factory for the antiforgery service. Create one instance per application
/// (it is stateless and safe to share across requests) and either invoke it
/// directly or attach it to a context through
/// <see cref="HttpContextAntiforgeryExtensions"/>.
/// </summary>
public static class HttpAntiforgery
{
    /// <summary>
    /// Creates an antiforgery service with default options, optionally
    /// customized by <paramref name="configure"/>.
    /// </summary>
    /// <param name="configure">An optional callback to adjust the
    /// <see cref="HttpAntiforgeryOptions"/> before the service is built.</param>
    /// <returns>A shareable <see cref="IHttpAntiforgery"/> instance.</returns>
    public static IHttpAntiforgery Create(Action<HttpAntiforgeryOptions>? configure = null)
    {
        HttpAntiforgeryOptions options = new();
        configure?.Invoke(options);
        return new HttpAntiforgeryService(options);
    }

    /// <summary>
    /// Creates an antiforgery service from the supplied options.
    /// </summary>
    /// <param name="options">The antiforgery options.</param>
    /// <returns>A shareable <see cref="IHttpAntiforgery"/> instance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static IHttpAntiforgery Create(HttpAntiforgeryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new HttpAntiforgeryService(options);
    }
}
