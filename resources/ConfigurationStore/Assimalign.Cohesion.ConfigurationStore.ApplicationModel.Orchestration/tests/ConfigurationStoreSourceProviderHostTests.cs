using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// Drives the public provider (and its default transport) against a real, authenticating
/// ConfigurationStore host, with the control-plane address a gateway hands it.
/// </summary>
public sealed class ConfigurationStoreSourceProviderHostTests
{
    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: Reads a namespace from a running store")]
    public async Task ReadConfigurationAsync_FromRunningStore_ShouldReturnNamespaceValues()
    {
        // Arrange
        await using RunningConfigurationStore store = RunningConfigurationStore.Create(builder =>
            builder.AddNamespace("app", values => values
                .Set("Mode", "development")
                .Set("Optional", null)));
        await store.StartAsync();
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider();
        ResourceSourceRequest request = SourceRequestFactory.Request(
            SourceRequestFactory.Connection(
                store.ControlPlaneAddress,
                store.Identity.Issue(RunningConfigurationStore.StoreResourceName)),
            key: "app");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        IReadOnlyDictionary<string, string?> values = await provider.ReadConfigurationAsync(request, timeout.Token);

        // Assert
        values.Count.ShouldBe(2);
        values["Mode"].ShouldBe("development");
        values.ContainsKey("Optional").ShouldBeTrue();
        values["Optional"].ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: A namespace the store does not hold is HTTP 404")]
    public async Task ReadConfigurationAsync_MissingNamespaceOnRunningStore_ShouldThrowNotFound()
    {
        // Arrange
        await using RunningConfigurationStore store = RunningConfigurationStore.Create(builder =>
            builder.AddNamespace("app", values => values.Set("Mode", "development")));
        await store.StartAsync();
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider();
        ResourceSourceRequest request = SourceRequestFactory.Request(
            SourceRequestFactory.Connection(
                store.ControlPlaneAddress,
                store.Identity.Issue(RunningConfigurationStore.StoreResourceName)),
            key: "missing");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        HttpRequestException exception = await Should.ThrowAsync<HttpRequestException>(
            async () => await provider.ReadConfigurationAsync(request, timeout.Token));

        // Assert
        exception.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: A credential for another audience is HTTP 403")]
    public async Task ReadConfigurationAsync_WithWrongAudienceCredential_ShouldThrowForbidden()
    {
        // Arrange
        await using RunningConfigurationStore store = RunningConfigurationStore.Create(builder =>
            builder.AddNamespace("app", values => values.Set("Mode", "development")));
        await store.StartAsync();
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider();
        ResourceSourceRequest request = SourceRequestFactory.Request(
            SourceRequestFactory.Connection(store.ControlPlaneAddress, store.Identity.Issue("api")),
            key: "app");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        HttpRequestException exception = await Should.ThrowAsync<HttpRequestException>(
            async () => await provider.ReadConfigurationAsync(request, timeout.Token));

        // Assert
        exception.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: A credential the application key did not sign is HTTP 401")]
    public async Task ReadConfigurationAsync_WithForeignCredential_ShouldThrowUnauthorized()
    {
        // Arrange
        await using RunningConfigurationStore store = RunningConfigurationStore.Create(builder =>
            builder.AddNamespace("app", values => values.Set("Mode", "development")));
        await store.StartAsync();
        using var foreign = new TestBootstrapIdentity();
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider();
        ResourceSourceRequest request = SourceRequestFactory.Request(
            SourceRequestFactory.Connection(
                store.ControlPlaneAddress,
                foreign.Issue(RunningConfigurationStore.StoreResourceName)),
            key: "app");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        HttpRequestException exception = await Should.ThrowAsync<HttpRequestException>(
            async () => await provider.ReadConfigurationAsync(request, timeout.Token));

        // Assert
        exception.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
