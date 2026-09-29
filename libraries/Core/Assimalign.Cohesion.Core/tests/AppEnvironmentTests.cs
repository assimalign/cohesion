using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Core.Tests;

public class AppEnvironmentTests
{
    private const string DisplayPrefix = "Cohesion Test [Core] - AppEnvironment: ";

    [Fact(DisplayName = DisplayPrefix + "Endpoint names use upper-snake normalization")]
    public void Endpoint_WithMixedNameAndSuffix_ShouldReturnCanonicalName()
    {
        // Arrange & Act
        string variable = AppEnvironment.Variables.Endpoint("public.http-v2", "port");

        // Assert
        variable.ShouldBe("COHESION_ENDPOINT_PUBLIC_HTTP_V2_PORT");
    }

    [Fact(DisplayName = DisplayPrefix + "Name normalization is ASCII and ordinal")]
    public void Endpoint_WithNonAsciiCharacters_ShouldReplaceEachNonAsciiCharacter()
    {
        // Arrange & Act
        string variable = AppEnvironment.Variables.Endpoint("résumé", "host");

        // Assert
        variable.ShouldBe("COHESION_ENDPOINT_R_SUM__HOST");
    }

    [Fact(DisplayName = DisplayPrefix + "Dependency names normalize resource and endpoint independently")]
    public void Dependency_WithMixedNames_ShouldReturnCanonicalName()
    {
        // Arrange & Act
        string variable = AppEnvironment.Variables.Dependency("orders-db", "admin.http", "url");

        // Assert
        variable.ShouldBe("COHESION_DEPENDENCY_ORDERS_DB_ADMIN_HTTP_URL");
    }

    [Fact(DisplayName = DisplayPrefix + "Mount names preserve one underscore per replaced character")]
    public void Mount_WithRepeatedPunctuation_ShouldNotCollapseUnderscores()
    {
        // Arrange & Act
        string variable = AppEnvironment.Variables.Mount("client--cert");

        // Assert
        variable.ShouldBe("COHESION_MOUNT_CLIENT__CERT_PATH");
    }

    [Fact(DisplayName = DisplayPrefix + "Configuration names use double-underscore section separation")]
    public void Configuration_WithSectionAndKey_ShouldPreserveConfigurationNames()
    {
        // Arrange & Act
        string variable = AppEnvironment.Variables.Configuration("Logging", "LogLevel");

        // Assert
        variable.ShouldBe("COHESION_CONFIG__Logging__LogLevel");
    }

    [Fact(DisplayName = DisplayPrefix + "Endpoint reader returns the bind address from a dictionary")]
    public void TryGetEndpoint_WithBindComponents_ShouldReturnUri()
    {
        // Arrange
        IDictionary<string, string?> environment = new Dictionary<string, string?>
        {
            [AppEnvironment.Variables.Endpoint("http", "HOST")] = "127.0.0.1",
            [AppEnvironment.Variables.Endpoint("http", "PORT")] = "5080",
            [AppEnvironment.Variables.Endpoint("http", "SCHEME")] = "http",
            [AppEnvironment.Variables.Endpoint("http", "PUBLIC_URL")] = "https://example.test:443/public"
        };

        // Act
        bool found = AppEnvironment.TryGetEndpoint(environment, "http", out Uri? address);

        // Assert
        found.ShouldBeTrue();
        address.ShouldBe(new Uri("http://127.0.0.1:5080"));
    }

    [Fact(DisplayName = DisplayPrefix + "Public endpoint URL remains separate from the bind address")]
    public void TryGetEndpoint_WithOnlyPublicUrl_ShouldRequireTypedUriReader()
    {
        // Arrange
        IDictionary<string, string?> environment = new Dictionary<string, string?>
        {
            [AppEnvironment.Variables.Endpoint("http", "PUBLIC_URL")] = "https://example.test:8443/public"
        };

        // Act
        bool foundAddress = AppEnvironment.TryGetEndpoint(environment, "http", out Uri? address);
        bool foundUrl = AppEnvironment.TryGetUri(
            environment,
            AppEnvironment.Variables.Endpoint("http", "PUBLIC_URL"),
            out Uri? publicUrl);

        // Assert
        foundAddress.ShouldBeFalse();
        address.ShouldBeNull();
        foundUrl.ShouldBeTrue();
        publicUrl.ShouldBe(new Uri("https://example.test:8443/public"));
    }

    [Fact(DisplayName = DisplayPrefix + "Dependency reader returns the observed URL")]
    public void TryGetDependency_WithObservedUrl_ShouldReturnUri()
    {
        // Arrange
        IDictionary<string, string?> environment = new Dictionary<string, string?>
        {
            [AppEnvironment.Variables.Dependency("orders", "grpc", "URL")] = "https://orders.test:7443/v1"
        };

        // Act
        bool found = AppEnvironment.TryGetDependency(environment, "orders", "grpc", out Uri? address);

        // Assert
        found.ShouldBeTrue();
        address.ShouldBe(new Uri("https://orders.test:7443/v1"));
    }

    [Fact(DisplayName = DisplayPrefix + "Dependency reader rejects a custom-scheme URL without a port")]
    public void TryGetDependency_WithCustomSchemeWithoutPort_ShouldReturnFalse()
    {
        // Arrange
        IDictionary<string, string?> environment = new Dictionary<string, string?>
        {
            [AppEnvironment.Variables.Dependency("orders", "tcp", "URL")] = "tcp://orders.test"
        };

        // Act
        bool found = AppEnvironment.TryGetDependency(environment, "orders", "tcp", out Uri? address);

        // Assert
        found.ShouldBeFalse();
        address.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "Typed readers reject malformed ports and accept absolute URIs")]
    public void TypedReaders_WithDictionaryValues_ShouldReturnTypedResults()
    {
        // Arrange
        IDictionary<string, string?> environment = new Dictionary<string, string?>
        {
            [AppEnvironment.Variables.Endpoint("admin", "PORT")] = "70000",
            [AppEnvironment.Variables.TelemetryEndpoint] = "http://collector.test:4318"
        };

        // Act
        bool foundPort = AppEnvironment.TryGetPort(
            environment,
            AppEnvironment.Variables.Endpoint("admin", "PORT"),
            out int port);
        bool foundUri = AppEnvironment.TryGetUri(
            environment,
            AppEnvironment.Variables.TelemetryEndpoint,
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
        string variable = AppEnvironment.Variables.Endpoint("contract-process-reader", "PORT");
        string? previousValue = Environment.GetEnvironmentVariable(variable);

        try
        {
            Environment.SetEnvironmentVariable(variable, "4317");

            // Act
            bool found = AppEnvironment.TryGetPort(variable, out int port);

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
            [AppEnvironment.Variables.Mount("secrets")] = "/run/secrets/app"
        };

        // Act
        bool found = AppEnvironment.TryGetMount(environment, "secrets", out string? path);

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
        AppEnvironment.Keys.EnvironmentKey.ShouldBe(AppEnvironment.Variables.Environment);
    }
}
