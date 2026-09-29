using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.Gateway.Tests;

/// <summary>
/// Verifies, through the packed Gateway SDK, that gateways compose resources with the
/// hand-written verbs of ApplicationModel packages over the generated manifests.
/// </summary>
public sealed class TypedResourceSdkIntegrationTests
{
    /// <summary>
    /// Verifies a third-party ApplicationModel package's own verb composes a generated manifest
    /// without any SDK-side mapping.
    /// </summary>
    /// <returns>The asynchronous package-boundary verification.</returns>
    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - third-party ApplicationModel package verb composes a generated manifest")]
    public async Task Build_ThirdPartyApplicationModelPackage_ShouldComposeThroughPackageVerb()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create(
            "ThirdPartyApplicationModel",
            "ThirdPartyResource",
            "ThirdPartyGateway");
        DotNetBuildResult pack = await workspace.PackAsync(
            "ThirdPartyApplicationModel",
            cancellationSource.Token);
        pack.ExitCode.ShouldBe(0, pack.Output);

        // Act: Program.cs calls builder.AddThirdParty(Manifests.ThirdPartyResource).
        DotNetBuildResult build = await workspace.BuildAsync(
            "ThirdPartyGateway",
            cancellationSource.Token);

        // Assert
        build.ExitCode.ShouldBe(0, build.Output);
        string source = ReadGeneratedSource(workspace, "ThirdPartyGateway");
        source.ShouldContain(
            "public static readonly global::Assimalign.Cohesion.ApplicationModel.ResourceManifest ThirdPartyResource",
            Case.Sensitive);
        source.ShouldNotContain("CohesionGatewayResourceExtensions", Case.Sensitive);
        source.ShouldNotContain("AddThirdPartyResource(", Case.Sensitive);
        source.ShouldNotContain("ThirdPartyResourceExtensions", Case.Sensitive);
    }

    /// <summary>
    /// Builds each area fixture whose gateway calls the area's hand-written verb, which compiles
    /// only when the SDK injected that area's ApplicationModel for the referenced project.
    /// </summary>
    /// <param name="area">The resource area named by the referenced project's SDK.</param>
    /// <param name="resourceFixture">The resource project that produces the manifest.</param>
    /// <param name="gatewayFixture">The gateway project consuming that manifest.</param>
    /// <param name="member">The expected manifest member derived from the resource name.</param>
    /// <returns>The asynchronous package-boundary verification.</returns>
    [Theory(DisplayName = "Cohesion Test [Sdk.Gateway] - Build: Should inject the referenced area's ApplicationModel for its hand-written verb")]
    [InlineData("IdentityHub", "TypedIdentityHub", "IdentityHubGateway", "TypedIdentity")]
    [InlineData("Rezolvr", "TypedRezolvr", "RezolvrGateway", "TypedRezolvr")]
    [InlineData("LogSpace", "TypedLogSpace", "LogSpaceGateway", "TypedLogs")]
    public async Task Build_TypedArea_ShouldInjectApplicationModelForHandWrittenVerb(
        string area,
        string resourceFixture,
        string gatewayFixture,
        string member)
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create(resourceFixture, gatewayFixture);

        // Act: Program.cs calls builder.Add<Area>(Manifests.<Member>) and keeps the typed descriptor.
        DotNetBuildResult build = await workspace.BuildAsync(gatewayFixture, cancellationSource.Token);

        // Assert
        build.ExitCode.ShouldBe(0, build.Output);
        string source = ReadGeneratedSource(workspace, gatewayFixture);
        source.ShouldContain(
            $"public static readonly global::Assimalign.Cohesion.ApplicationModel.ResourceManifest {member}",
            Case.Sensitive);
        source.ShouldNotContain("CohesionGatewayResourceExtensions", Case.Sensitive);
        source.ShouldNotContain($"Add{member}(", Case.Sensitive);
        source.ShouldNotContain($"{area}ResourceExtensions", Case.Sensitive);
    }

    private static string ReadGeneratedSource(ConsumerWorkspace workspace, string fixtureName)
    {
        string sourcePath = Directory.EnumerateFiles(
            Path.Combine(workspace.ProjectDirectory(fixtureName), "obj"),
            "Gateway.g.cs",
            SearchOption.AllDirectories).Single();
        return File.ReadAllText(sourcePath);
    }
}
