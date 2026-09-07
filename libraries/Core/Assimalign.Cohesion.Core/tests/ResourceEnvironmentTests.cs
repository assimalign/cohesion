using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.Core.Tests;

public class ResourceEnvironmentTests
{
    private const string DisplayPrefix = "Cohesion Test [Core] - ResourceEnvironment: ";

    [Fact(DisplayName = DisplayPrefix + "Endpoint names use upper-snake normalization")]
    public void Endpoint_WithMixedNameAndSuffix_ShouldReturnCanonicalName()
    {
        // Arrange & Act
        string variable = ResourceEnvironment.Endpoint("public.http-v2", "port");

        // Assert
        variable.ShouldBe("COHESION_ENDPOINT_PUBLIC_HTTP_V2_PORT");
    }

    [Fact(DisplayName = DisplayPrefix + "Name normalization is ASCII and ordinal")]
    public void Endpoint_WithNonAsciiCharacters_ShouldReplaceEachNonAsciiCharacter()
    {
        // Arrange & Act
        string variable = ResourceEnvironment.Endpoint("résumé", "host");

        // Assert
        variable.ShouldBe("COHESION_ENDPOINT_R_SUM__HOST");
    }

    [Fact(DisplayName = DisplayPrefix + "Dependency names normalize resource and endpoint independently")]
    public void Dependency_WithMixedNames_ShouldReturnCanonicalName()
    {
        // Arrange & Act
        string variable = ResourceEnvironment.Dependency("orders-db", "admin.http", "url");

        // Assert
        variable.ShouldBe("COHESION_DEPENDENCY_ORDERS_DB_ADMIN_HTTP_URL");
    }

    [Fact(DisplayName = DisplayPrefix + "Mount names preserve one underscore per replaced character")]
    public void Mount_WithRepeatedPunctuation_ShouldNotCollapseUnderscores()
    {
        // Arrange & Act
        string variable = ResourceEnvironment.Mount("client--cert");

        // Assert
        variable.ShouldBe("COHESION_MOUNT_CLIENT__CERT_PATH");
    }

    [Fact(DisplayName = DisplayPrefix + "Configuration names use double-underscore section separation")]
    public void Configuration_WithSectionAndKey_ShouldPreserveConfigurationNames()
    {
        // Arrange & Act
        string variable = ResourceEnvironment.Configuration("Logging", "LogLevel");

        // Assert
        variable.ShouldBe("COHESION_CONFIG__Logging__LogLevel");
    }

    [Fact(DisplayName = DisplayPrefix + "Endpoint reader returns the bind address from a dictionary")]
    public void TryGetEndpoint_WithBindComponents_ShouldReturnEndpointAddress()
    {
        // Arrange
        IDictionary<string, string?> environment = new Dictionary<string, string?>
        {
            [ResourceEnvironment.Endpoint("http", "HOST")] = "127.0.0.1",
            [ResourceEnvironment.Endpoint("http", "PORT")] = "5080",
            [ResourceEnvironment.Endpoint("http", "SCHEME")] = "http",
            [ResourceEnvironment.Endpoint("http", "PUBLIC_URL")] = "https://example.test:443/public"
        };

        // Act
        bool found = ResourceEnvironment.TryGetEndpoint(environment, "http", out EndpointAddress address);

        // Assert
        found.ShouldBeTrue();
        address.ShouldBe(new EndpointAddress("http", "127.0.0.1", 5080));
    }

    [Fact(DisplayName = DisplayPrefix + "Public endpoint URL remains separate from the bind address")]
    public void TryGetEndpoint_WithOnlyPublicUrl_ShouldRequireTypedUriReader()
    {
        // Arrange
        IDictionary<string, string?> environment = new Dictionary<string, string?>
        {
            [ResourceEnvironment.Endpoint("http", "PUBLIC_URL")] = "https://example.test:8443/public"
        };

        // Act
        bool foundAddress = ResourceEnvironment.TryGetEndpoint(environment, "http", out EndpointAddress address);
        bool foundUrl = ResourceEnvironment.TryGetUri(
            environment,
            ResourceEnvironment.Endpoint("http", "PUBLIC_URL"),
            out Uri? publicUrl);

        // Assert
        foundAddress.ShouldBeFalse();
        address.ShouldBe(default);
        foundUrl.ShouldBeTrue();
        publicUrl.ShouldBe(new Uri("https://example.test:8443/public"));
    }

    [Fact(DisplayName = DisplayPrefix + "Dependency reader returns the observed URL")]
    public void TryGetDependency_WithObservedUrl_ShouldReturnEndpointAddress()
    {
        // Arrange
        IDictionary<string, string?> environment = new Dictionary<string, string?>
        {
            [ResourceEnvironment.Dependency("orders", "grpc", "URL")] = "https://orders.test:7443/v1"
        };

        // Act
        bool found = ResourceEnvironment.TryGetDependency(environment, "orders", "grpc", out EndpointAddress address);

        // Assert
        found.ShouldBeTrue();
        address.ShouldBe(new EndpointAddress("https", "orders.test", 7443, "/v1"));
    }

    [Fact(DisplayName = DisplayPrefix + "Typed readers reject malformed ports and accept absolute URIs")]
    public void TypedReaders_WithDictionaryValues_ShouldReturnTypedResults()
    {
        // Arrange
        IDictionary<string, string?> environment = new Dictionary<string, string?>
        {
            [ResourceEnvironment.Endpoint("admin", "PORT")] = "70000",
            [ResourceEnvironment.TelemetryEndpoint] = "http://collector.test:4318"
        };

        // Act
        bool foundPort = ResourceEnvironment.TryGetPort(
            environment,
            ResourceEnvironment.Endpoint("admin", "PORT"),
            out int port);
        bool foundUri = ResourceEnvironment.TryGetUri(
            environment,
            ResourceEnvironment.TelemetryEndpoint,
            out Uri? uri);

        // Assert
        foundPort.ShouldBeFalse();
        port.ShouldBe(0);
        foundUri.ShouldBeTrue();
        uri.ShouldBe(new Uri("http://collector.test:4318"));
    }

    [Fact(DisplayName = DisplayPrefix + "Process reader returns a typed port")]
    public void TryGetPort_WithProcessEnvironment_ShouldReturnTypedPort()
    {
        // Arrange
        string variable = ResourceEnvironment.Endpoint("contract-process-reader", "PORT");
        string? previousValue = Environment.GetEnvironmentVariable(variable);

        try
        {
            Environment.SetEnvironmentVariable(variable, "4317");

            // Act
            bool found = ResourceEnvironment.TryGetPort(variable, out int port);

            // Assert
            found.ShouldBeTrue();
            port.ShouldBe(4317);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previousValue);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "Mount reader returns a non-empty path")]
    public void TryGetMount_WithDictionaryPath_ShouldReturnPath()
    {
        // Arrange
        IDictionary<string, string?> environment = new Dictionary<string, string?>
        {
            [ResourceEnvironment.Mount("secrets")] = "/run/secrets/app"
        };

        // Act
        bool found = ResourceEnvironment.TryGetMount(environment, "secrets", out string? path);

        // Assert
        found.ShouldBeTrue();
        path.ShouldBe("/run/secrets/app");
    }

    [Fact(DisplayName = DisplayPrefix + "Application environment follows Cohesion, .NET, Production precedence")]
    public void GetEnvironmentName_WithDictionary_ShouldUseOnePrecedenceRule()
    {
        // Arrange
        IDictionary<string, string?> cohesion = new Dictionary<string, string?>
        {
            [AppEnvironment.Keys.EnvironmentKey] = "Staging",
            [AppEnvironment.Keys.DotNetEnvironmentKey] = "Development"
        };
        IDictionary<string, string?> dotNet = new Dictionary<string, string?>
        {
            [AppEnvironment.Keys.DotNetEnvironmentKey] = "Development"
        };
        IDictionary<string, string?> empty = new Dictionary<string, string?>();

        // Act & Assert
        AppEnvironment.GetEnvironmentName(cohesion).ShouldBe("Staging");
        AppEnvironment.GetEnvironmentName(dotNet).ShouldBe("Development");
        AppEnvironment.GetEnvironmentName(empty).ShouldBe(AppEnvironment.Keys.DefaultEnvironmentName);
        AppEnvironment.Keys.EnvironmentKey.ShouldBe(ResourceEnvironment.Environment);
    }
}
