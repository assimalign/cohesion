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
            "GatewaySmokeDatabase", "CommandConfigurationStore", "CommandSecretStore", "CommandGateway");

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
        source.ShouldContain("public global::Assimalign.Cohesion.SecretStore.ApplicationModel.ISecretStoreResourceDescriptor AddCommandSecrets(global::System.Action<global::Assimalign.Cohesion.SecretStore.ApplicationModel.SecretStoreResourceOptions>? configure = null)", Case.Sensitive);
        source.ShouldContain("global::Assimalign.Cohesion.SecretStore.ApplicationModel.SecretStoreResourceExtensions.AddSecretStore(builder, Manifests.CommandSecrets, options)", Case.Sensitive);
        source.ShouldNotContain("builder.AddResource(", Case.Sensitive);
        File.ReadAllLines(Path.Combine(directory, "obj", "command-clients.txt"))
            .ShouldBe(new[]
            {
                "Assimalign.Cohesion.ConfigurationStore.Client",
                "Assimalign.Cohesion.Database.Client",
                "Assimalign.Cohesion.SecretStore.Client"
            });
        string[] references = File.ReadAllLines(Path.Combine(directory, "obj", "command-reference-paths.txt"));
        references.ShouldContain("Assimalign.Cohesion.Database.Client");
        references.ShouldContain("Assimalign.Cohesion.ConfigurationStore.Client");
        references.ShouldContain("Assimalign.Cohesion.SecretStore.Client");
        references.ShouldNotContain("Assimalign.Cohesion.Database.Hosting");
        references.ShouldNotContain("Assimalign.Cohesion.ConfigurationStore.Hosting");
        references.ShouldNotContain("Assimalign.Cohesion.SecretStore.Hosting");

        // Act: compare manifest commands with the area's factory, excluding protocol bootstrap commands.
        DotNetBuildResult describe = await workspace.RunBuiltProjectAsync(
            "CommandGateway", ["--mode=describe", "--gateway=local", "--environment=Local"], cancellationSource.Token);

        // Assert
        describe.ExitCode.ShouldBe(0, describe.Output);
    }
}
