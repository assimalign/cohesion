using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Component factories for <see cref="IHttpClientFactory"/>. These exist so a composition
/// surface in another assembly can be handed a ready-made factory without this library
/// referencing that surface.
/// </summary>
/// <remarks>
/// Nothing here names a service container. The Cohesion component-integration generator
/// projects the declarations in <c>Properties/ComponentIntegrations.cs</c> into a verb on
/// the consuming compilation, and only when the named seam assembly is referenced there.
/// This preserves the "no DI surface" posture documented in <c>docs/DESIGN.md</c>.
/// </remarks>
public static class HttpClientFactoryComponents
{
    /// <summary>
    /// Composes a named-client factory from <paramref name="configure"/> and returns a
    /// deferred producer for it.
    /// </summary>
    /// <param name="configure">Registers named clients and factory-wide settings.</param>
    /// <returns>A producer that yields the composed <see cref="IHttpClientFactory"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">No named client was registered.</exception>
    public static Func<IServiceProvider, IHttpClientFactory> CreateHttpClientFactory(
        Action<HttpClientFactoryBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        // Composed eagerly so HttpClientFactoryBuilder.Build()'s "at least one named client"
        // guard (HttpClientFactoryBuilder.cs:74-82) fires at registration time rather than at
        // first resolve. Constructing the factory allocates only options plus a TimeProvider;
        // handlers are created lazily in Create(name).
        var builder = new HttpClientFactoryBuilder();
        configure.Invoke(builder);
        IHttpClientFactory factory = builder.Build();

        // Returned as a PRODUCER, not an instance. A container registration made from an
        // instance becomes a ConstantCallSite, which the resolver returns without calling
        // CaptureDisposable - the pooled handlers would never be released. A producer
        // registration becomes a root-cached FactoryCallSite, which is captured for disposal.
        return _ => factory;
    }
}
