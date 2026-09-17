using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

[Collection(LocalGatewayConsoleCollection.Name)]
public class ApplicationGatewayOwnershipTests
{
    private const string ApplicationNameValue = "owner-tests";
    private const string TestHostAssembly = "Assimalign.Cohesion.ApplicationModel.Gateway.TestHost";

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Ownership: Should refuse a foreign owner and require explicit adopt")]
    public async Task StartAsync_WithForeignOwner_ShouldRefuseUntilAdopted()
    {
        // Arrange
        string root = Path.Combine(
            Path.GetTempPath(),
            "cohesion-owner-" + Guid.NewGuid().ToString("N"));
        string stateDirectory = Path.Combine(root, ".cohesion");
        string ownerDirectory = Path.Combine(stateDirectory, ApplicationNameValue, ".state");
        string ownerPath = Path.Combine(ownerDirectory, "owner");
        Directory.CreateDirectory(ownerDirectory);
        await File.WriteAllTextAsync(ownerPath, "other@kubernetes" + Environment.NewLine);
        IApplicationGateway? activeGateway = null;
        bool started = false;

        try
        {
            var refusingGateway = new LocalGateway(CreateOptions(stateDirectory));
            IApplication refusingApplication = BuildApplication(refusingGateway, []);

            // Act
            InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
                () => ((IApplicationGateway)refusingGateway).StartAsync(refusingApplication.Model));

            // Assert
            exception.Message.ShouldContain("other@kubernetes");
            exception.Message.ShouldContain("owner-tests@local");
            exception.Message.ShouldContain("--adopt");
            (await File.ReadAllTextAsync(ownerPath)).Trim().ShouldBe("other@kubernetes");

            var adoptingGateway = new LocalGateway(CreateOptions(stateDirectory));
            activeGateway = adoptingGateway;
            IApplication adoptingApplication = BuildApplication(adoptingGateway, ["--adopt"]);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            await ((IApplicationGateway)adoptingGateway)
                .StartAsync(adoptingApplication.Model, cancellation.Token);
            started = true;

            (await File.ReadAllTextAsync(ownerPath)).Trim().ShouldBe("owner-tests@local");
            await ((IApplicationGateway)adoptingGateway).StopAsync(cancellation.Token);
            started = false;
        }
        finally
        {
            if (started && activeGateway is not null)
            {
                await activeGateway.StopAsync(CancellationToken.None);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local lifecycle: Stop should retain mounts and Uninstall should remove them")]
    public async Task StopAsync_ThenUninstallAsync_ShouldRetainThenDeletePersistentLocalObjects()
    {
        // Arrange
        string root = Path.Combine(
            Path.GetTempPath(),
            "cohesion-uninstall-" + Guid.NewGuid().ToString("N"));
        string stateDirectory = Path.Combine(root, ".cohesion");
        var gateway = new LocalGateway(CreateOptions(stateDirectory));
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse(ApplicationNameValue),
                [])
            .UseGateway(gateway);
        builder.AddResource(CreateManifest(includeMount: true));
        IApplicationModel model = builder.Build().Model;
        string resourceDirectory = Path.Combine(stateDirectory, ApplicationNameValue, "worker");
        string mountPath = Path.Combine(resourceDirectory, "settings");

        try
        {
            // Act
            await ((IApplicationGateway)gateway).StartAsync(model);
            await ((IApplicationGateway)gateway).StopAsync();

            // Assert
            File.Exists(mountPath).ShouldBeTrue();

            await ((IApplicationGateway)gateway).UninstallAsync(model);

            Directory.Exists(resourceDirectory).ShouldBeFalse();
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static LocalGatewayOptions CreateOptions(string stateDirectory) => new()
    {
        BaseDirectory = AppContext.BaseDirectory,
        StateDirectory = stateDirectory,
        ProbeInterval = TimeSpan.FromMilliseconds(50),
        ProbeTimeout = TimeSpan.FromSeconds(2),
        ReadinessBudget = TimeSpan.FromSeconds(10),
        StopGrace = TimeSpan.FromSeconds(5),
    };

    private static IApplication BuildApplication(LocalGateway gateway, string[] args)
    {
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse(ApplicationNameValue),
                args)
            .UseGateway(gateway);
        builder.AddResource(CreateManifest());
        return builder.Build();
    }

    private static ResourceManifest CreateManifest(bool includeMount = false) => new()
    {
        Name = "worker",
        Application = ApplicationNameValue,
        Kind = "test",
        ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
        Artifact = new ResourceManifestArtifact
        {
            Assembly = TestHostAssembly + ".dll",
            AppHost = Path.Combine(
                AppContext.BaseDirectory,
                TestHostAssembly + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)),
        },
        Endpoints =
        [
            new ResourceManifestEndpoint
            {
                Name = "http",
                Scheme = "http",
                Protocol = "tcp",
                ContainerPort = 8080,
            },
        ],
        Probes = new ResourceManifestProbes
        {
            Startup = new ResourceManifestProbe { None = true },
            Readiness = new ResourceManifestProbe { None = true },
            Liveness = new ResourceManifestProbe { None = true },
        },
        ControlPlane = new ResourceManifestControlPlane
        {
            Endpoint = "http",
            Path = "/cohesion/v1",
        },
        Mounts = includeMount
            ?
            [
                new ResourceManifestMount
                {
                    Name = "settings",
                    Kind = ResourceMountKind.Configuration,
                    ContainerPath = "/inputs/settings",
                    Source = "literal:value",
                },
            ]
            : Array.Empty<ResourceManifestMount>(),
        Lifecycle = new ResourceManifestLifecycle
        {
            RestartPolicy = "Never",
            StopGraceSeconds = 5,
        },
    };
}
