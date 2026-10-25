using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tests;

public sealed class ResourceDescribeSdkIntegrationTests
{
    private const string DisplayPrefix = "Cohesion Test [Sdk] - describe: ";
    private const string SurfaceFileName = "resource.runtime.json";

    [Fact(DisplayName = DisplayPrefix + "an enabled resource's build writes its runtime surface beside the output")]
    public async Task Build_EnabledWebResource_WritesItsRuntimeSurfaceAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("EnabledWebExplicitMain", "DescribedWeb");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync("DescribedWeb", timeout.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        result.Output.ShouldNotContain("COHSDK012");
        string described = workspace.ProjectDirectory("DescribedWeb");
        using (JsonDocument surface = JsonDocument.Parse(File.ReadAllText(OutputFile(described, SurfaceFileName))))
        {
            surface.RootElement.GetProperty("schema").GetString().ShouldBe("cohesion/runtime/v1");
            JsonElement section = surface.RootElement.GetProperty("configuration").EnumerateArray().ShouldHaveSingleItem();
            section.GetProperty("path").GetString().ShouldBe("Described:Limits");
            section.GetProperty("mutability").GetString().ShouldBe("live");
            section.GetProperty("schema").GetProperty("properties").TryGetProperty("MaxItems", out _).ShouldBeTrue();
        }

        // The reference resolved to the placeholder in the referenced endpoint's own scheme and container port.
        File.ReadAllText(OutputFile(described, "described-reference.txt")).ShouldBe("http://cohesion-describe.invalid:8080");

        // The referenced resource was built, and described, as a dependency of this build.
        using JsonDocument referenced = JsonDocument.Parse(File.ReadAllText(
            OutputFile(workspace.ProjectDirectory("EnabledWebExplicitMain"), SurfaceFileName)));
        referenced.RootElement.GetProperty("configuration").GetArrayLength().ShouldBe(0);
    }

    [Fact(DisplayName = DisplayPrefix + "a resource that fails before it builds its host warns and keeps the build")]
    public async Task Build_ResourceFailingBeforeItsHost_WarnsWithoutFailingAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("UndescribableWeb");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync("UndescribableWeb", timeout.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        result.Output.ShouldContain("warning COHSDK012", Case.Sensitive);
        result.Output.ShouldContain("needs a live dependency before it builds its host", Case.Sensitive);
        FindOutputFile(workspace.ProjectDirectory("UndescribableWeb"), SurfaceFileName).ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "CohesionDescribeResource=false skips the run")]
    public async Task Build_WithDescribeDisabled_WritesNoSurfaceAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("UndescribableWeb");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync("UndescribableWeb", ["CohesionDescribeResource=false"], timeout.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        result.Output.ShouldNotContain("COHSDK012");
        FindOutputFile(workspace.ProjectDirectory("UndescribableWeb"), SurfaceFileName).ShouldBeNull();
    }

    private static string OutputFile(string projectDirectory, string fileName) =>
        FindOutputFile(projectDirectory, fileName)
            ?? throw new FileNotFoundException($"'{fileName}' was not written under '{projectDirectory}'.", fileName);

    private static string? FindOutputFile(string projectDirectory, string fileName)
    {
        string output = Path.Combine(projectDirectory, "bin");
        return Directory.Exists(output)
            ? Directory.EnumerateFiles(output, fileName, SearchOption.AllDirectories).SingleOrDefault()
            : null;
    }
}
