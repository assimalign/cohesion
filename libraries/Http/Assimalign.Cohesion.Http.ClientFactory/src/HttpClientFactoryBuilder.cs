using System;

namespace Assimalign.Cohesion.Http;

using Assimalign.Cohesion.Http.Internal;

/// <summary>
/// Fluent builder for <see cref="HttpClientFactory"/>. Registers named clients and
/// factory-wide settings and produces an <see cref="IHttpClientFactory"/> when
/// <see cref="Build"/> is called.
/// </summary>
public sealed class HttpClientFactoryBuilder
{
    private readonly HttpClientFactoryOptions _options = new();

    /// <summary>
    /// Sets the factory-wide default handler lifetime. Overridable per-name via
    /// <see cref="NamedHttpClientOptions.HandlerLifetime"/>. Default is two minutes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lifetime"/> is non-positive.</exception>
    public HttpClientFactoryBuilder WithDefaultHandlerLifetime(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "Handler lifetime must be positive.");
        }
        _options.DefaultHandlerLifetime = lifetime;
        return this;
    }

    /// <summary>
    /// Sets the time source used for handler-expiration calculations. Tests pin a
    /// controllable provider here to advance the clock deterministically.
    /// </summary>
    public HttpClientFactoryBuilder WithTimeProvider(TimeProvider provider)
    {
        _options.TimeProvider = provider ?? throw new ArgumentNullException(nameof(provider));
        return this;
    }

    /// <summary>
    /// Registers a named client. Calling
    /// <see cref="IHttpClientFactory.Create(string)"/> with <paramref name="name"/> after
    /// <see cref="Build"/> returns clients configured per <paramref name="configure"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty, or a
    /// client with the same name has already been registered.</exception>
    public HttpClientFactoryBuilder AddClient(string name, Action<NamedHttpClientOptions>? configure = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Client name must be non-empty.", nameof(name));
        }
        if (_options.NamedClients.ContainsKey(name))
        {
            throw new ArgumentException(
                $"A client with the name '{name}' has already been registered.",
                nameof(name));
        }

        var clientOptions = new NamedHttpClientOptions();
        configure?.Invoke(clientOptions);
        _options.NamedClients[name] = clientOptions;
        return this;
    }

    /// <summary>
    /// Builds the factory. The returned instance owns the rotating handler pool and must
    /// be disposed (synchronously via <see cref="IDisposable"/> or asynchronously via
    /// <see cref="System.IAsyncDisposable"/>) at application shutdown to release
    /// the pooled handlers.
    /// </summary>
    /// <exception cref="InvalidOperationException">No named clients have been registered.</exception>
    public IHttpClientFactory Build()
    {
        if (_options.NamedClients.Count == 0)
        {
            throw new InvalidOperationException(
                "At least one named client must be registered before the factory can be built.");
        }
        return new HttpClientFactory(_options);
    }
}
