using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.Gateway.Tests;

/// <summary>Verifies typed resource mappings through the packed Gateway SDK.</summary>
public sealed class TypedResourceSdkIntegrationTests
{
    /// <summary>Builds each area fixture and checks its generated typed composition method.</summary>
    /// <param name="area">The mapped resource area.</param>
    /// <param name="resourceFixture">The resource project that produces the manifest.</param>
    /// <param name="gatewayFixture">The gateway project consuming that manifest.</param>
    /// <param name="member">The expected manifest member derived from the resource name.</param>
    /// <returns>The asynchronous package-boundary verification.</returns>
    [Theory(DisplayName = "Cohesion Test [Sdk.Gateway] - Build: Should preserve typed area options descriptors and Add methods")]
    [InlineData("IdentityHub", "TypedIdentityHub", "IdentityHubGateway", "TypedIdentity")]
    [InlineData("Rezolvr", "TypedRezolvr", "RezolvrGateway", "TypedRezolvr")]
    [InlineData("LogSpace", "TypedLogSpace", "LogSpaceGateway", "TypedLogs")]
    public async Task Build_TypedArea_ShouldGenerateTypedDescriptorAndAddMethod(
        string area,
        string resourceFixture,
        string gatewayFixture,
        string member)
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create(resourceFixture, gatewayFixture);

        // Act
        DotNetBuildResult build = await workspace.BuildAsync(gatewayFixture, cancellationSource.Token);

        // Assert
        build.ExitCode.ShouldBe(0, build.Output);
        string sourcePath = Directory.EnumerateFiles(
            Path.Combine(workspace.ProjectDirectory(gatewayFixture), "obj"),
            "Gateway.g.cs",
            SearchOption.AllDirectories).Single();
        string source = File.ReadAllText(sourcePath);
        string areaNamespace = $"global::Assimalign.Cohesion.{area}.ApplicationModel";
        source.ShouldContain(
            $"public {areaNamespace}.I{area}ResourceDescriptor Add{member}(global::System.Action<{areaNamespace}.{area}ResourceOptions>? configure = null)",
            Case.Sensitive);
        source.ShouldContain(
            $"{areaNamespace}.{area}ResourceExtensions.Add{area}(builder, Manifests.{member}, options)",
            Case.Sensitive);
        source.ShouldNotContain("builder.AddResource(", Case.Sensitive);
    }
}
