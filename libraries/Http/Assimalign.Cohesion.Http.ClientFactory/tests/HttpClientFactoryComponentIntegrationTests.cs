using System;
using System.Net.Http;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Http.ClientFactory.Tests;

/// <summary>
/// Behavioral tests for the component-integration projection onto
/// <see cref="IServiceProviderBuilder"/>.
/// </summary>
public class HttpClientFactoryComponentIntegrationTests
{
    [Fact(DisplayName = "Cohesion Test [Http.ClientFactory] - AddHttpClientFactory: resolves a configured named-client factory")]
    public void AddHttpClientFactory_WithNamedClient_ShouldResolveConfiguredFactory()
    {
        // Arrange
        var builder = new ServiceProviderBuilder();
        var expectedBaseAddress = new Uri("https://example.test/");

        // Act
        IServiceProvider provider = builder
            .AddHttpClientFactory(clients => clients.AddClient(
                "test",
                options => options.BaseAddress = expectedBaseAddress))
            .Build();

        try
        {
            IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();
            using HttpClient client = factory.Create("test");

            // Assert
            factory.ShouldNotBeNull();
            client.ShouldNotBeNull();
            client.BaseAddress.ShouldBe(expectedBaseAddress);
        }
        finally
        {
            ((IDisposable)provider).Dispose();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Http.ClientFactory] - AddHttpClientFactory: a bare call throws at registration and registers nothing")]
    public void AddHttpClientFactory_CalledBare_ShouldThrowInvalidOperationException()
    {
        // Arrange — the projected callback is optional (owner decision 39), but the builder still
        // requires a named client, and the adapter builds eagerly, so the call itself must fail.
        var builder = new ServiceProviderBuilder();

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => builder.AddHttpClientFactory());
        builder.Container.Count.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.ClientFactory] - AddHttpClientFactory: provider disposal disposes the resolved factory")]
    public void AddHttpClientFactory_WhenProviderDisposed_ShouldDisposeResolvedFactory()
    {
        // Arrange
        var builder = new ServiceProviderBuilder();
        IServiceProvider provider = builder
            .AddHttpClientFactory(clients => clients.AddClient("test"))
            .Build();
        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();

        // Act
        ((IDisposable)provider).Dispose();

        // Assert — Create is the strongest public disposal signal exposed by the factory.
        Should.Throw<ObjectDisposedException>(() => factory.Create("test"));
    }

    [Fact(DisplayName = "Cohesion Test [Http.ClientFactory] - AddHttpClientFactory: provider callback resolves dependencies at first resolve and retains singleton disposal")]
    public void AddHttpClientFactory_WithServiceProvider_ShouldConfigureOnFirstResolve()
    {
        // Arrange
        var builder = new ServiceProviderBuilder();
        var expectedBaseAddress = new Uri("https://example.test/provider/");
        int configureCalls = 0;
        IServiceProvider? callbackProvider = null;

        // Act
        builder.AddHttpClientFactory((services, clients) =>
        {
            configureCalls++;
            callbackProvider = services;
            Uri address = services.GetRequiredService<Uri>();
            clients.AddClient("CohesionDocs", options => options.BaseAddress = address);
        }).ShouldBeSameAs(builder);
        IServiceProvider provider = builder.AddSingleton(expectedBaseAddress).Build();
        IHttpClientFactory factory;
        try
        {
            // Assert
            configureCalls.ShouldBe(0);
            factory = provider.GetRequiredService<IHttpClientFactory>();
            callbackProvider.ShouldBeSameAs(provider.GetRequiredService<IServiceProvider>());
            configureCalls.ShouldBe(1);
            provider.GetRequiredService<IHttpClientFactory>().ShouldBeSameAs(factory);
            configureCalls.ShouldBe(1);
            using HttpClient client = factory.Create("CohesionDocs");
            client.BaseAddress.ShouldBe(expectedBaseAddress);
        }
        finally
        {
            ((IDisposable)provider).Dispose();
        }
        Should.Throw<ObjectDisposedException>(() => factory.Create("CohesionDocs"));
    }

    [Fact(DisplayName = "Cohesion Test [Http.ClientFactory] - AddHttpClientFactory: provider callback validates the builder at resolve")]
    public void AddHttpClientFactory_WithServiceProviderAndNoClients_ShouldThrowOnResolve()
    {
        // Arrange
        var builder = new ServiceProviderBuilder();

        // Act
        IServiceProvider provider = builder.AddHttpClientFactory((services, clients) => { }).Build();
        try
        {
            // Assert
            Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<IHttpClientFactory>());
        }
        finally
        {
            ((IDisposable)provider).Dispose();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Http.ClientFactory] - AddHttpClientFactory: typed null provider callback fails before registration")]
    public void AddHttpClientFactory_WithNullServiceProviderCallback_ShouldThrowAtRegistration()
    {
        // Arrange
        var builder = new ServiceProviderBuilder();
        Action<IServiceProvider, HttpClientFactoryBuilder> configure = null!;

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => builder.AddHttpClientFactory(configure))
            .ParamName.ShouldBe("configure");
        builder.Container.Count.ShouldBe(0);
    }
}
