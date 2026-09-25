using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

[Collection(LocalGatewayConsoleCollection.Name)]
public class LocalRealizedExternalTests
{
    private const string ConsumerApplication = "consumer-app";
    private const string RetainedApplication = "peer-app";
    private const string Resource = "external-api";
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local --realize: Should retain the external manifest application lifecycle")]
    public async Task StartAsync_RealizedExternal_ShouldRetainManifestApplicationAndStopCleanly()
    {
        // Arrange
        string root = Path.Combine(
            Path.GetTempPath(),
            $"cohesion-local-realize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string environmentCapture = Path.Combine(root, "environment.json");
        string bound = Path.Combine(root, "bound.txt");
        string stopObserved = Path.Combine(root, "stop-observed.txt");
        string processPath = Path.Combine(
            root,
            ".cohesion",
            RetainedApplication,
            ".state",
            Resource,
            "pid");
        string ownerPath = Path.Combine(
            root,
            ".cohesion",
            RetainedApplication,
            ".state",
            "owner");
        string consumerProcessPath = Path.Combine(
            root,
            ".cohesion",
            ConsumerApplication,
            ".state",
            Resource,
            "pid");
        var gateway = new LocalGateway(
            new LocalGatewayOptions
            {
                BaseDirectory = AppContext.BaseDirectory,
                StateDirectory = Path.Combine(root, ".cohesion"),
                ExportDirectory = Path.Combine(root, "exports"),
                ProbeInterval = TimeSpan.FromMilliseconds(50),
                ProbeTimeout = TimeSpan.FromSeconds(2),
                ReadinessBudget = TimeSpan.FromSeconds(10),
                StopGrace = TimeSpan.FromSeconds(2),
            });
        ExternalResourceDeclaration declaration = CreateDeclaration(
            environmentCapture,
            bound,
            stopObserved);
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse(ConsumerApplication),
                ["--environment", AppEnvironment.Keys.Local, "--realize", Resource])
            .UseGateway(gateway);
        IApplicationResourceDescriptor external = builder.RemoteReference(
            declaration,
            options => options.Endpoint("http", "http://peer.invalid:8080"));
        IApplicationModel model = builder.Build().Model;
        IApplicationGateway control = gateway;
        using var cancellation = new CancellationTokenSource(_testTimeout);
        int processId = 0;
        bool stopped = false;

        try
        {
            // Act
            await control.StartAsync(model, cancellation.Token);

            // Assert
            File.Exists(bound).ShouldBeTrue();
            File.Exists(environmentCapture).ShouldBeTrue();
            File.Exists(processPath).ShouldBeTrue();
            File.Exists(consumerProcessPath).ShouldBeFalse();
            File.ReadAllText(ownerPath).Trim().ShouldBe($"{RetainedApplication}@local");

            using (JsonDocument environment = JsonDocument.Parse(
                       await File.ReadAllBytesAsync(environmentCapture, cancellation.Token)))
            {
                environment.RootElement
                    .GetProperty(ResourceEnvironment.Application)
                    .GetString()
                    .ShouldBe(RetainedApplication);
                environment.RootElement
                    .GetProperty(ResourceEnvironment.Resource)
                    .GetString()
                    .ShouldBe(Resource);
            }

            processId = ReadProcessId(processPath);
            ProcessExists(processId).ShouldBeTrue();
            gateway.ResourceStates.GetState(external.Resource.Id)
                .ShouldBe(ResourceLifecycle.Running);

            await control.StopAsync(cancellation.Token);
            stopped = true;

            File.Exists(stopObserved).ShouldBeTrue();
            File.Exists(processPath).ShouldBeFalse();
            ProcessExists(processId).ShouldBeFalse();
            gateway.ResourceStates.GetState(external.Resource.Id)
                .ShouldBe(ResourceLifecycle.Stopped);
        }
        finally
        {
            if (!stopped)
            {
                await TryStopAsync(control);
            }

            TryTerminate(processId);
            DeleteDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local --realize: Should name an unbound parameter mount")]
    public async Task StartAsync_RealizedExternalHasUnboundParameter_ShouldNameParameterAndMount()
    {
        // Arrange
        string root = Path.Combine(
            Path.GetTempPath(),
            $"cohesion-local-realize-parameter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var gateway = new LocalGateway(
            new LocalGatewayOptions
            {
                BaseDirectory = AppContext.BaseDirectory,
                StateDirectory = Path.Combine(root, ".cohesion"),
                ExportDirectory = Path.Combine(root, "exports"),
                ReadinessBudget = TimeSpan.FromSeconds(2),
            });
        ExternalResourceDeclaration original = CreateDeclaration(
            Path.Combine(root, "environment.json"),
            Path.Combine(root, "bound.txt"),
            Path.Combine(root, "stop-observed.txt"));
        ResourceManifest manifest = original.Manifest! with
        {
            Mounts =
            [
                new ResourceManifestMount
                {
                    Name = "credentials",
                    ContainerPath = "/inputs/credentials",
                    Kind = ResourceMountKind.Secret,
                    Source = "parameter:peer-token",
                },
            ],
        };
        var declaration = new ExternalResourceDeclaration(
            original.Name,
            original.Application,
            original.ReferencedEndpoints,
            original.Optional,
            manifest,
            [manifest]);
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse(ConsumerApplication),
                ["--environment", AppEnvironment.Keys.Local, "--realize", Resource])
            .UseGateway(gateway);
        builder.RemoteReference(
            declaration,
            options => options.Endpoint("http", "http://peer.invalid:8080"));
        IApplicationModel model = builder.Build().Model;

        try
        {
            // Act
            InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
                () => ((IApplicationGateway)gateway).StartAsync(model));

            // Assert
            exception.Message.ShouldContain("peer-token", Case.Sensitive);
            exception.Message.ShouldContain("credentials", Case.Sensitive);
            exception.Message.ShouldContain(Resource, Case.Sensitive);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static ExternalResourceDeclaration CreateDeclaration(
        string environmentCapture,
        string bound,
        string stopObserved)
    {
        var none = new ResourceManifestProbe { None = true };
        return new ExternalResourceDeclaration(
            Resource,
            RetainedApplication,
            ["http"],
            optional: false,
            new ResourceManifest
            {
                Name = Resource,
                Application = RetainedApplication,
                Kind = "test",
                ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
                Artifact = new ResourceManifestArtifact
                {
                    Assembly = "Assimalign.Cohesion.ApplicationModel.Gateway.TestHost.dll",
                    AppHost = TestHostPath,
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
                    Startup = none,
                    Readiness = none,
                    Liveness = none,
                },
                ControlPlane = new ResourceManifestControlPlane
                {
                    Endpoint = "http",
                    Path = "/cohesion/v1",
                },
                EnvironmentVariables = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["TEST_ENV_CAPTURE_PATH"] = environmentCapture,
                    ["TEST_BOUND_PATH"] = bound,
                    ["TEST_STOP_OBSERVED_PATH"] = stopObserved,
                },
                Lifecycle = new ResourceManifestLifecycle
                {
                    Workload = WorkloadKind.Deployment,
                    RestartPolicy = "Never",
                    StopGraceSeconds = 2,
                },
            });
    }

    private static string TestHostPath => Path.Combine(
        AppContext.BaseDirectory,
        "Assimalign.Cohesion.ApplicationModel.Gateway.TestHost"
        + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));

    private static int ReadProcessId(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("processId").GetInt32();
    }

    private static bool ProcessExists(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task TryStopAsync(IApplicationGateway gateway)
    {
        try
        {
            await gateway.StopAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // Test cleanup remains best-effort so the original assertion is preserved.
        }
    }

    private static void TryTerminate(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(milliseconds: 5000);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void DeleteDirectory(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
        }
    }
}
