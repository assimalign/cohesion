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
}
