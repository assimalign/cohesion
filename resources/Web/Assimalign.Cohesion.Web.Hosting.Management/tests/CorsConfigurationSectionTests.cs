using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Cors;
using Assimalign.Cohesion.Web.Hosting.Management;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Management.Tests;

public sealed class CorsConfigurationSectionTests : IDisposable
{
    private const string DisplayPrefix = "Cohesion Test [Web.Hosting.Management] - CORS section: ";
    private const string Portal = "https://portal.example";
    private const string Partner = "https://partner.example";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cohesion-cors-record-" + Guid.NewGuid().ToString("N"));

    public CorsConfigurationSectionTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact(DisplayName = DisplayPrefix + "The default section is the live, unclaimable Web:Cors section")]
    public void AsResourceConfiguration_DefaultPath_DeclaresALiveWebCorsSection()
    {
        // Arrange
        CorsPolicyStore store = CorsPolicyStore.Create(DefaultPolicy(Portal));

        // Act
        ResourceConfigurationSection section = store.AsResourceConfiguration();

        // Assert
        section.Path.Value.ShouldBe("Web:Cors");
        section.Mutability.ShouldBe(ResourceConfigurationMutability.Live);
        section.Scope.ShouldBe(ResourceConfigurationScope.Process);
        section.Claimable.ShouldBeFalse();
        section.Schema.GetProperty("properties").TryGetProperty("defaultPolicy", out _).ShouldBeTrue();
        section.Schema.GetProperty("properties").TryGetProperty("policies", out _).ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "An application can publish a store at its own path")]
    public void AsResourceConfiguration_WithPath_UsesThePath()
    {
        // Arrange
        CorsPolicyStore store = CorsPolicyStore.Create(new CorsConfiguration());

        // Act
        ResourceConfigurationSection section = store.AsResourceConfiguration("Web:Cors:Admin");

        // Assert
        section.Path.Value.ShouldBe("Web:Cors:Admin");
    }

    [Fact(DisplayName = DisplayPrefix + "A null store or a default path is refused")]
    public void AsResourceConfiguration_NullStoreOrDefaultPath_ShouldThrow()
    {
        // Arrange
        CorsPolicyStore store = CorsPolicyStore.Create(new CorsConfiguration());

        // Act
        Should.Throw<ArgumentNullException>(() => ((CorsPolicyStore)null!).AsResourceConfiguration());
        Should.Throw<ArgumentException>(() => store.AsResourceConfiguration(default(ResourceConfigurationPath)));
    }

    [Fact(DisplayName = DisplayPrefix + "An override replaces the policies that later requests select")]
    public async Task ApplyConfigurationAsync_OverrideChangesOrigins_ReplacesStorePoliciesAsync()
    {
        // Arrange
        CorsPolicyStore store = CorsPolicyStore.Create(DefaultPolicy(Portal));
        ResourceControlPlane plane = CreatePlane(store);
        Write("override.json", """{"Web:Cors":{"defaultPolicy":{"origins":["https://partner.example"]}}}""");

        // Act
        ResourceConfigurationStatus status = await plane.ApplyConfigurationAsync(
            ResourceConfigurationRecord.Load(_directory), ResourceConfigurationPhase.Running, CancellationToken.None);

        // Assert
        status.Sections[0].State.ShouldBe(ResourceConfigurationState.Applied);
        store.GetConfiguration().DefaultPolicy!.Origins.ShouldBe([Partner]);
    }

    [Fact(DisplayName = DisplayPrefix + "Removing the override restores the configured baseline")]
    public async Task ApplyConfigurationAsync_OverrideRemoved_RestoresTheBaselineAsync()
    {
        // Arrange
        CorsPolicyStore store = CorsPolicyStore.Create(DefaultPolicy(Portal));
        ResourceControlPlane plane = CreatePlane(store);
        Write("override.json", """{"Web:Cors":{"defaultPolicy":{"origins":["https://partner.example"]}}}""");
        await plane.ApplyConfigurationAsync(ResourceConfigurationRecord.Load(_directory), ResourceConfigurationPhase.Running, CancellationToken.None);
        File.Delete(Path.Combine(_directory, "override.json"));

        // Act
        ResourceConfigurationStatus status = await plane.ApplyConfigurationAsync(
            ResourceConfigurationRecord.Load(_directory), ResourceConfigurationPhase.Running, CancellationToken.None);

        // Assert
        status.Sections[0].State.ShouldBe(ResourceConfigurationState.Baseline);
        store.GetConfiguration().DefaultPolicy!.Origins.ShouldBe([Portal]);
    }

    [Fact(DisplayName = DisplayPrefix + "Removing a named policy is refused and every policy stays in force")]
    public async Task ApplyConfigurationAsync_NamedPolicyRemoved_RejectsAndKeepsPoliciesAsync()
    {
        // Arrange
        var configuration = new CorsConfiguration();
        configuration.Policies.Add("spa", new CorsPolicyConfiguration { Origins = [Portal] });
        CorsPolicyStore store = CorsPolicyStore.Create(configuration);
        ResourceControlPlane plane = CreatePlane(store);
        Write("override.json", """{"Web:Cors":{"policies":{"spa":null}}}""");

        // Act
        ResourceConfigurationStatus status = await plane.ApplyConfigurationAsync(
            ResourceConfigurationRecord.Load(_directory), ResourceConfigurationPhase.Running, CancellationToken.None);

        // Assert
        status.Sections[0].State.ShouldBe(ResourceConfigurationState.Rejected);
        status.Sections[0].Error.ShouldNotBeNull().ShouldStartWith("cors-invalid: ", Case.Sensitive);
        store.GetConfiguration().Policies["spa"].Origins.ShouldBe([Portal]);
    }

    [Fact(DisplayName = DisplayPrefix + "A policy CORS forbids is refused before any of it is published")]
    public async Task ApplyConfigurationAsync_CredentialedWildcard_RejectsAndKeepsPoliciesAsync()
    {
        // Arrange
        CorsPolicyStore store = CorsPolicyStore.Create(DefaultPolicy(Portal));
        ResourceControlPlane plane = CreatePlane(store);
        Write("override.json", """{"Web:Cors":{"defaultPolicy":{"allowAnyOrigin":true,"allowCredentials":true}}}""");

        // Act
        ResourceConfigurationStatus status = await plane.ApplyConfigurationAsync(
            ResourceConfigurationRecord.Load(_directory), ResourceConfigurationPhase.Running, CancellationToken.None);

        // Assert
        status.Sections[0].State.ShouldBe(ResourceConfigurationState.Rejected);
        store.GetConfiguration().DefaultPolicy!.AllowAnyOrigin.ShouldBeFalse();
        store.GetConfiguration().DefaultPolicy!.Origins.ShouldBe([Portal]);
    }

    [Fact(DisplayName = DisplayPrefix + "A member the configuration does not define is refused as invalid")]
    public async Task ApplyConfigurationAsync_UnknownMember_RejectsAsInvalidValueAsync()
    {
        // Arrange
        CorsPolicyStore store = CorsPolicyStore.Create(DefaultPolicy(Portal));
        ResourceControlPlane plane = CreatePlane(store);
        Write("deployment.json", """{"Web:Cors":{"defaultPolicy":{"origin":["https://partner.example"]}}}""");

        // Act
        ResourceConfigurationStatus status = await plane.ApplyConfigurationAsync(
            ResourceConfigurationRecord.Load(_directory), ResourceConfigurationPhase.Startup, CancellationToken.None);

        // Assert
        status.Sections[0].State.ShouldBe(ResourceConfigurationState.Rejected);
        status.Sections[0].Error.ShouldNotBeNull().ShouldStartWith("invalid-value: ", Case.Sensitive);
        store.GetConfiguration().DefaultPolicy!.Origins.ShouldBe([Portal]);
    }

    private static ResourceControlPlane CreatePlane(CorsPolicyStore store)
    {
        ResourceControlPlane plane = ResourceControlPlane.Create();
        plane.AddConfiguration(store.AsResourceConfiguration());
        return plane;
    }

    private static CorsConfiguration DefaultPolicy(string origin) =>
        new() { DefaultPolicy = new CorsPolicyConfiguration { Origins = [origin] } };

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_directory, name), content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}
