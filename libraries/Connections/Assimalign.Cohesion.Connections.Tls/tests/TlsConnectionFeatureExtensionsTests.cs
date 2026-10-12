using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tls.Tests.TestObjects;

namespace Assimalign.Cohesion.Connections.Tls.Tests;

public class TlsConnectionFeatureExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Connections.Tls] - Tls: A connection returns its registered feature")]
    public async Task Tls_OnConnectionWithFeature_ShouldReturnRegisteredFeature()
    {
        // Arrange
        (Connection client, Connection server) = InMemoryConnectionPair.Create();
        await using Connection clientLifetime = client;
        await using Connection serverLifetime = server;
        TestTlsFeature feature = new();
        client.Features.Set(feature);

        // Act
        ITlsConnectionInfo? tls = ((IConnection)client).Tls;

        // Assert
        tls.ShouldBeSameAs(feature);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tls] - Tls: A connection without the feature returns null")]
    public async Task Tls_OnConnectionWithoutFeature_ShouldReturnNull()
    {
        // Arrange
        (Connection client, Connection server) = InMemoryConnectionPair.Create();
        await using Connection clientLifetime = client;
        await using Connection serverLifetime = server;

        // Act
        ITlsConnectionInfo? tls = ((IConnection)client).Tls;

        // Assert
        tls.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tls] - Tls: A multiplexed connection returns its registered feature")]
    public async Task Tls_OnMultiplexedConnectionWithFeature_ShouldReturnRegisteredFeature()
    {
        // Arrange
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        await using MultiplexedConnection clientLifetime = client;
        await using MultiplexedConnection serverLifetime = server;
        TestTlsFeature feature = new();
        client.Features.Set(feature);

        // Act
        ITlsConnectionInfo? tls = ((IMultiplexedConnection)client).Tls;

        // Assert
        tls.ShouldBeSameAs(feature);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tls] - Tls: A multiplexed connection without the feature returns null")]
    public async Task Tls_OnMultiplexedConnectionWithoutFeature_ShouldReturnNull()
    {
        // Arrange
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        await using MultiplexedConnection clientLifetime = client;
        await using MultiplexedConnection serverLifetime = server;

        // Act
        ITlsConnectionInfo? tls = ((IMultiplexedConnection)client).Tls;

        // Assert
        tls.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tls] - FeatureName: The TLS slot name is pinned")]
    public void FeatureName_ForTls_ShouldBePinnedValue()
    {
        // Arrange
        TestTlsFeature feature = new();

        // Act
        string name = feature.Name;

        // Assert
        ITlsConnectionInfo.FeatureName.ShouldBe("Tls");
        name.ShouldBe(ITlsConnectionInfo.FeatureName);
    }
}
