using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.Gateway.Tests;

public sealed class ResourceCommandSdkIntegrationTests
{
    private static readonly string[] _areaClientPackages =
    [
        "Assimalign.Cohesion.ConfigurationStore.Client",
        "Assimalign.Cohesion.Database.Client",
        "Assimalign.Cohesion.SecretStore.Client"
    ];

    [Fact(DisplayName = "Cohesion Test [Sdk.Gateway] - command-bearing resources compose through area verbs with no generated verbs or client packages")]
    public async Task Build_CommandTargets_ShouldGenerateNoVerbsAndInjectNoClientPackages()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create(
            "GatewaySmokeDatabase", "CommandConfigurationStore", "CommandSecretStore", "CommandGateway");

        // Act: Program.cs composes each resource with its area verb and declares typed commands.
        DotNetBuildResult build = await workspace.BuildAsync("CommandGateway", cancellationSource.Token);

        // Assert: the generated surface is manifests only.
        build.ExitCode.ShouldBe(0, build.Output);
        string directory = workspace.ProjectDirectory("CommandGateway");
        string sourcePath = Directory.EnumerateFiles(
            Path.Combine(directory, "obj"), "Gateway.g.cs", SearchOption.AllDirectories).Single();
        string source = File.ReadAllText(sourcePath);
        source.ShouldContain("ResourceManifest GatewaySmokeDatabase", Case.Sensitive);
        source.ShouldContain("ResourceManifest CommandConfiguration", Case.Sensitive);
        source.ShouldContain("ResourceManifest CommandSecrets", Case.Sensitive);
        source.ShouldNotContain("CohesionGatewayResourceExtensions", Case.Sensitive);
        source.ShouldNotContain("AddGatewaySmokeDatabase(", Case.Sensitive);
        source.ShouldNotContain("AddCommandConfiguration(", Case.Sensitive);
        source.ShouldNotContain("AddCommandSecrets(", Case.Sensitive);

        // Assert: Sdk.SecretStore marks add-secret RequiresInputResolver in the manifest the gateway reads.
        string secretsManifestPath = Directory.EnumerateFiles(
            Path.Combine(workspace.ProjectDirectory("CommandSecretStore"), "obj"), "resource.json", SearchOption.AllDirectories)
            .First();
        using (JsonDocument secretsManifest = JsonDocument.Parse(File.ReadAllText(secretsManifestPath)))
        {
            JsonElement[] commands = secretsManifest.RootElement.GetProperty("commands").EnumerateArray().ToArray();
            commands.ShouldContain(static command => command.ValueKind == JsonValueKind.Object &&
                command.GetProperty("kind").GetString() == "secretstore.add-secret" &&
                command.GetProperty("requiresInputResolver").GetBoolean());
            commands.ShouldContain(static command => command.ValueKind == JsonValueKind.String &&
                command.GetString() == "secretstore.issue-certificate");
        }

        // Assert: the SDK injects the referenced areas' ApplicationModel packages and no client.
        string[] packages = File.ReadAllLines(Path.Combine(directory, "obj", "command-package-references.txt"));
        packages.ShouldContain("Assimalign.Cohesion.Database.ApplicationModel");
        packages.ShouldContain("Assimalign.Cohesion.ConfigurationStore.ApplicationModel");
        packages.ShouldContain("Assimalign.Cohesion.SecretStore.ApplicationModel");
        foreach (string client in _areaClientPackages)
        {
            packages.ShouldNotContain(client);
        }

        // Assert: no area client or runtime reaches the out-of-process gateway's compilation.
        string[] references = File.ReadAllLines(Path.Combine(directory, "obj", "command-reference-paths.txt"));
        foreach (string client in _areaClientPackages)
        {
            references.ShouldNotContain(client);
        }
        references.ShouldNotContain("Assimalign.Cohesion.Database.Hosting");
        references.ShouldNotContain("Assimalign.Cohesion.ConfigurationStore.Hosting");
        references.ShouldNotContain("Assimalign.Cohesion.SecretStore.Hosting");

        // Act: describe calls Build(), which requires the fixture's add-secret input resolver, then
        // compares manifest commands with the area's factory, excluding protocol bootstrap commands.
        DotNetBuildResult describe = await workspace.RunBuiltProjectAsync(
            "CommandGateway", ["--mode=describe", "--gateway=local", "--environment=Local"], cancellationSource.Token);

        // Assert
        describe.ExitCode.ShouldBe(0, describe.Output);
    }
}
