using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.Gateway.Tests;

public sealed class ResourceCommandSdkIntegrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - typed commands resolve clients and match default control-plane kinds")]
    public async Task Build_CommandTargets_PreservesTypedDescriptorsAndResolvesCommandClients()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create(
            "GatewaySmokeDatabase", "CommandConfigurationStore", "CommandGateway");

        // Act
        DotNetBuildResult build = await workspace.BuildAsync("CommandGateway", cancellationSource.Token);

        // Assert
        build.ExitCode.ShouldBe(0, build.Output);
        string directory = workspace.ProjectDirectory("CommandGateway");
        string sourcePath = Directory.EnumerateFiles(
            Path.Combine(directory, "obj"), "Gateway.g.cs", SearchOption.AllDirectories).Single();
        string source = File.ReadAllText(sourcePath);
        source.ShouldContain("public global::Assimalign.Cohesion.Database.ApplicationModel.IDatabaseResourceDescriptor AddGatewaySmokeDatabase(", Case.Sensitive);
        source.ShouldContain("public global::Assimalign.Cohesion.ConfigurationStore.ApplicationModel.IConfigurationStoreResourceDescriptor AddCommandConfiguration(", Case.Sensitive);
        File.ReadAllLines(Path.Combine(directory, "obj", "command-clients.txt"))
            .ShouldBe(new[] { "Assimalign.Cohesion.ConfigurationStore.Client", "Assimalign.Cohesion.Database.Client" });
        string[] references = File.ReadAllLines(Path.Combine(directory, "obj", "command-reference-paths.txt"));
        references.ShouldContain("Assimalign.Cohesion.Database.Client");
        references.ShouldContain("Assimalign.Cohesion.ConfigurationStore.Client");
        references.ShouldNotContain("Assimalign.Cohesion.Database.Hosting");
        references.ShouldNotContain("Assimalign.Cohesion.ConfigurationStore.Hosting");

        // Act: the fixture compares each emitted manifest's command kinds directly with its area's runtime factory.
        DotNetBuildResult describe = await workspace.RunBuiltProjectAsync(
            "CommandGateway", ["--mode=describe", "--gateway=local", "--environment=Development"], cancellationSource.Token);

        // Assert
        describe.ExitCode.ShouldBe(0, describe.Output);
    }
}
