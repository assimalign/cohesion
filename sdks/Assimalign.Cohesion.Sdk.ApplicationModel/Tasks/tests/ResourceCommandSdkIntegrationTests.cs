using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tests;

public sealed class ResourceCommandSdkIntegrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sdk] - ConfigurationStore advertises its bounded command kinds")]
    public async Task Build_ConfigurationStore_EmitsDefaultCommandStrings()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("EnabledConfigurationStore");

        // Act
        DotNetBuildResult result = await workspace.BuildAsync("EnabledConfigurationStore", cancellationSource.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        using JsonDocument manifest = ReadManifest(workspace, "EnabledConfigurationStore");
        manifest.RootElement.GetProperty("commands").EnumerateArray().Select(command => command.GetString())
            .ShouldBe(new[] { "configurationstore.add-namespace", "configurationstore.remove-value", "configurationstore.set-value" });
    }

    [Fact(DisplayName = "Cohesion Test [Sdk] - CohesionCommand items produce deterministic bare manifest strings")]
    public async Task Build_CommandItems_EmitsSortedUniqueStrings()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("EnabledGeneric");
        string project = workspace.ProjectFile("EnabledGeneric");
        File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>",
            "<ItemGroup><CohesionCommand Include=\"worker.z;worker.a;worker.z\" /></ItemGroup></Project>",
            StringComparison.Ordinal));

        // Act
        DotNetBuildResult result = await workspace.BuildAsync("EnabledGeneric", cancellationSource.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        using JsonDocument manifest = ReadManifest(workspace, "EnabledGeneric");
        manifest.RootElement.GetProperty("commands").EnumerateArray().Select(command => command.GetString())
            .ShouldBe(new[] { "worker.a", "worker.z" });
    }

    [Fact(DisplayName = "Cohesion Test [Sdk] - command items reject payload metadata")]
    public async Task Build_CommandWithPayloadMetadata_RejectsUnsupportedMetadata()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("EnabledGeneric");
        string project = workspace.ProjectFile("EnabledGeneric");
        File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>",
            "<ItemGroup><CohesionCommand Include=\"worker.start\" Payload=\"unsupported\" /></ItemGroup></Project>",
            StringComparison.Ordinal));

        // Act
        DotNetBuildResult result = await workspace.BuildAsync("EnabledGeneric", cancellationSource.Token);

        // Assert
        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.ShouldContain("Unknown metadata 'Payload' on CohesionCommand 'worker.start'.", Case.Sensitive);
    }

    private static JsonDocument ReadManifest(ConsumerWorkspace workspace, string fixture)
    {
        string path = Directory.EnumerateFiles(
            Path.Combine(workspace.ProjectDirectory(fixture), "obj"), "resource.json", SearchOption.AllDirectories).Single();
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
