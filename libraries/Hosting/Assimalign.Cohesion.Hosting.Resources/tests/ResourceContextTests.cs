using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

public class ResourceContextTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - ResourceContext: ";

    [Fact(DisplayName = DisplayPrefix + "Environment carrier round-trips endpoints, mounts, and settings")]
    public void FromEnvironment_WithCarrierValues_RoundTripsValues()
    {
        // Arrange
        var environment = new Dictionary<string, string?>
        {
            [ResourceEnvironment.Application] = "example",
            [ResourceEnvironment.Resource] = "api",
            [ResourceEnvironment.Environment] = "Development",
            [ResourceEnvironment.Endpoint("http", "HOST")] = "127.0.0.1",
            [ResourceEnvironment.Endpoint("http", "PORT")] = "5080",
            [ResourceEnvironment.Endpoint("http", "SCHEME")] = "http",
            [ResourceEnvironment.Mount("settings")] = Path.GetFullPath("settings.json"),
            [ResourceEnvironment.Configuration("Orders", "PageSize")] = "50",
            [ResourceEnvironment.Dependency("database", "db", "URL")] = "tcp://database.internal:5432",
            [ResourceEnvironment.ApplicationTrustKey] = "{\"kty\":\"EC\"}",
        };

        // Act
        ResourceContext context = ResourceContext.FromEnvironment(environment);

        // Assert
        context.ApplicationName.ShouldBe("example");
        context.ResourceName.ShouldBe("api");
        context.EnvironmentName.ShouldBe("Development");
        context.GetEndpoint("http", "https", 7443).ShouldBe(
            new Uri("http://127.0.0.1:5080"));
        context.GetMount("settings", "/settings.json").Path.ShouldBe(
            Path.GetFullPath("settings.json"));
        context.GetSetting("Orders:PageSize", fallback: null).ShouldBe("50");
        context.GetReference("database", "db").ShouldBe(
            new Uri("tcp://database.internal:5432"));
        Encoding.UTF8.GetString(context.ApplicationTrustKey.Span).ShouldBe("{\"kty\":\"EC\"}");
        context.Endpoints.Count.ShouldBe(1);
        context.Mounts.Count.ShouldBe(1);
        context.Settings.Count.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "Scope identity enforces disposal order even for the same context")]
    public void CreateScope_WithSameContextNested_RejectsAndRecoversFromOutOfOrderDispose()
    {
        // Arrange
        var context = new ResourceContext(resourceName: "same");
        IDisposable outer = ResourceRuntime.CreateScope(context);
        IDisposable inner = ResourceRuntime.CreateScope(context);

        try
        {
            // Act & Assert
            Should.Throw<InvalidOperationException>(() => outer.Dispose());
            ResourceRuntime.Current.ShouldBeSameAs(context);

            inner.Dispose();
            outer.Dispose();
        }
        finally
        {
            inner.Dispose();
            outer.Dispose();
        }
    }

    [Fact(DisplayName = DisplayPrefix + "DevPort fallback is used only without a gateway")]
    public void GetEndpoint_WithDevPort_RequiresStandaloneContext()
    {
        // Arrange
        var standalone = new ResourceContext();
        var gateway = new ResourceContext(gatewayName: "local");

        // Act
        Uri endpoint = standalone.GetEndpoint("http", "http", 5080);

        // Assert
        endpoint.ShouldBe(new Uri("http://localhost:5080"));
        standalone.TryGetEndpoint("admin", "http", devPort: null, out _).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => gateway.GetEndpoint("http", "http", 5080));
    }

    [Theory(DisplayName = DisplayPrefix + "Constructor rejects endpoint-shaped dictionary values that are not endpoints")]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_WithInvalidEndpointDictionaryValue_ShouldThrowArgumentException(bool reference)
    {
        // Arrange
        IReadOnlyDictionary<string, Uri> values = new Dictionary<string, Uri>
        {
            [reference ? "database:db" : "http"] = new Uri("relative/path", UriKind.Relative)
        };

        // Act
        Action action = reference
            ? () => _ = new ResourceContext(references: values)
            : () => _ = new ResourceContext(endpoints: values);

        // Assert
        Should.Throw<ArgumentException>(action);
    }

    [Fact(DisplayName = DisplayPrefix + "Nested scopes restore and parallel scopes do not leak")]
    public async Task CreateScope_WithNestedAndParallelContexts_IsIsolated()
    {
        // Arrange
        var outer = new ResourceContext(resourceName: "outer");
        var inner = new ResourceContext(resourceName: "inner");

        // Act & Assert
        using (ResourceRuntime.CreateScope(outer))
        {
            ResourceRuntime.Current.ShouldBeSameAs(outer);
            using (ResourceRuntime.CreateScope(inner))
            {
                ResourceRuntime.Current.ShouldBeSameAs(inner);
            }

            ResourceRuntime.Current.ShouldBeSameAs(outer);

            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<string?> first = Task.Run(async () =>
            {
                using (ResourceRuntime.CreateScope(new ResourceContext(resourceName: "first")))
                {
                    await release.Task;
                    return ResourceRuntime.Current.ResourceName;
                }
            });
            Task<string?> second = Task.Run(async () =>
            {
                using (ResourceRuntime.CreateScope(new ResourceContext(resourceName: "second")))
                {
                    release.TrySetResult();
                    await Task.Yield();
                    return ResourceRuntime.Current.ResourceName;
                }
            });

            (await first).ShouldBe("first");
            (await second).ShouldBe("second");
            ResourceRuntime.Current.ShouldBeSameAs(outer);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "Connection factory resolvers remain invocation-local")]
    public void RegisterConnectionFactoryResolver_WithNestedScopes_DoesNotLeak()
    {
        // Arrange
        var outerFactory = new object();
        var innerFactory = new object();

        // Act & Assert
        using (ResourceRuntime.CreateScope(new ResourceContext(resourceName: "outer")))
        {
            ResourceRuntime.RegisterConnectionFactoryResolver(_ => outerFactory);
            ResourceRuntime.Current.GetConnectionFactory<object>("database", "db", "tcp")
                .ShouldBeSameAs(outerFactory);

            using (ResourceRuntime.CreateScope(new ResourceContext(resourceName: "inner")))
            {
                ResourceRuntime.RegisterConnectionFactoryResolver(_ => innerFactory);
                ResourceRuntime.Current.GetConnectionFactory<object>("database", "db", "tcp")
                    .ShouldBeSameAs(innerFactory);
            }

            ResourceRuntime.Current.GetConnectionFactory<object>("database", "db", "tcp")
                .ShouldBeSameAs(outerFactory);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "Mount reader returns plaintext for the platform carrier")]
    public void ReadAllBytes_WithMaterializedCarrier_ReturnsPlaintext()
    {
        // Arrange
        string path = Path.Combine(Path.GetTempPath(), $"cohesion-mount-{Guid.NewGuid():N}");
        byte[] plaintext = Encoding.UTF8.GetBytes("mount secret");
        byte[] persisted = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser)
            : plaintext;

        try
        {
            File.WriteAllBytes(path, persisted);
            var mount = new ResourceMount(path);
            ResourceContext context = ResourceContext.FromEnvironment(
                new Dictionary<string, string?>
                {
                    [ResourceEnvironment.BootstrapTokenPath] = path,
                });

            // Act
            byte[] read = mount.ReadAllBytes();
            using Stream stream = mount.OpenRead();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);

            // Assert
            read.ShouldBe(plaintext);
            copy.ToArray().ShouldBe(plaintext);
            mount.Path.ShouldBe(path);
            context.BootstrapCredential.ToArray().ShouldBe(plaintext);
        }
        finally
        {
            File.Delete(path);
            if (!ReferenceEquals(persisted, plaintext))
            {
                CryptographicOperations.ZeroMemory(persisted);
            }
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
