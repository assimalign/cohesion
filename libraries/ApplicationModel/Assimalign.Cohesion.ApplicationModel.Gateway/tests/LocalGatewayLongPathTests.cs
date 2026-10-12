using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

[Collection(LocalGatewayConsoleCollection.Name)]
public class LocalGatewayLongPathTests
{
    [WindowsLongPathFact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: launches long Windows paths with working content, mounts, and exec probes")]
    public async Task StartAsync_LongExecutableAndWorkingDirectory_LaunchesWithContentAndMounts()
    {
        // Arrange
        using var directory = new LongPathTestDirectory();
        directory.CreateExecutable(copyHost: true);
        File.WriteAllText(Path.Combine(directory.HostDirectory, "content.txt"), "content-root");
        string capture = Path.Combine(directory.Root, "environment.json");
        string probeStatus = Path.Combine(directory.Root, "probe.status");
        File.WriteAllText(probeStatus, "0");
        LocalGateway gateway = CreateGateway(directory);
        IApplicationModel model = CreateModel(directory, gateway, new Dictionary<string, string>
        {
            ["TEST_ENV_CAPTURE_PATH"] = capture,
            ["TEST_CAPTURE_PATHS"] = "true",
        }, new ResourceManifestProbe { Exec = [directory.ExecutablePath, "exec-probe", probeStatus] });
        IApplicationGateway control = gateway;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            // Act
            await control.StartAsync(model, cancellation.Token);

            // Assert
            directory.ExecutablePath.Length.ShouldBeGreaterThanOrEqualTo(260);
            directory.HostDirectory.Length.ShouldBeGreaterThanOrEqualTo(260);
            gateway.ResourceStates.GetState(model.Resources[0].Id).ShouldBe(ResourceLifecycle.Running);
            using JsonDocument environment = JsonDocument.Parse(File.ReadAllText(capture));
            JsonElement values = environment.RootElement;
            values.GetProperty("baseDirectory").GetString()!.Length.ShouldBeLessThan(260);
            // .NET can expand the inherited short current directory back to its long name.
            LocalProcessPath.TryGetShortPath(values.GetProperty("workingDirectory").GetString()!)
                .ShouldBe(LocalProcessPath.TryGetShortPath(directory.HostDirectory));
            values.GetProperty("workingContent").GetString().ShouldBe("content-root");
            values.GetProperty("baseContent").GetString().ShouldBe("content-root");
            values.GetProperty("rootContent").GetString().ShouldBe("content-root");
            values.GetProperty(AppEnvironment.Variables.ContentRoot).GetString().ShouldBe(directory.HostDirectory);
            string mountPath = values.GetProperty("mountPath").GetString()!;
            mountPath.Length.ShouldBeGreaterThanOrEqualTo(260);
            File.ReadAllText(Path.Combine(mountPath, "written-by-child.txt")).ShouldBe("mounted");
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
        }
    }

    [WindowsLongPathFact(false, DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: no short path surfaces the coded cause in reconcile")]
    public async Task StartAsync_NoShortPath_ThrowsActionableReconcileFailure()
    {
        // Arrange
        using var directory = new LongPathTestDirectory();
        directory.CreateExecutable(copyHost: false);
        int lookups = 0;
        LocalGateway gateway = CreateGateway(directory, path =>
        {
            path.ShouldBe(directory.ExecutablePath);
            lookups++;
            // GetShortPathName can succeed while returning the original long name.
            return path;
        });
        IApplicationModel model = CreateModel(directory, gateway);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token));

        // Assert
        lookups.ShouldBe(1);
        exception.Message.ShouldContain("observed 'Failed'", Case.Sensitive);
        exception.Message.ShouldContain("long-path-tests/host", Case.Sensitive);
        exception.Message.ShouldContain("COHLOCAL001", Case.Sensitive);
        exception.Message.ShouldContain($"{directory.ExecutablePath.Length} characters", Case.Sensitive);
        exception.Message.ShouldContain(directory.ExecutablePath, Case.Sensitive);
        exception.Message.ShouldContain("Move the application to a shorter path", Case.Sensitive);
        exception.Message.ShouldContain("enable 8.3 names", Case.Sensitive);
        File.Exists(Path.Combine(StateDirectory(directory), "long-path-tests", ".state", "host", "pid"))
            .ShouldBeFalse();
    }

    [WindowsLongPathFact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: adopts a short-path process with a long-path registration")]
    public async Task StartAsync_ShortPathOrphanWithLongPathIdentity_AdoptsWithoutRelaunch()
    {
        // Arrange
        using var directory = new LongPathTestDirectory();
        directory.CreateExecutable(copyHost: true);
        string shortPath = LocalProcessPath.TryGetShortPath(directory.ExecutablePath)!;
        string stopEventName = $"Local\\cohesion-long-path-{Guid.NewGuid():N}";
        using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, stopEventName);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = shortPath,
                WorkingDirectory = Path.GetDirectoryName(shortPath),
                UseShellExecute = false,
            },
        };
        if (OperatingSystem.IsWindows())
        {
            process.StartInfo.CreateNewProcessGroup = true;
        }

        process.StartInfo.ArgumentList.Add("wait");
        process.StartInfo.Environment[AppEnvironment.Variables.StopEvent] = stopEventName;
        LocalGateway gateway = CreateGateway(directory);
        IApplicationModel model = CreateModel(directory, gateway);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        process.Start().ShouldBeTrue();
        try
        {
            var store = new LocalProcessStateStore(StateDirectory(directory));
            await store.SaveAsync(model.Name, "host", new LocalProcessRegistration
            {
                RegistrationId = Guid.NewGuid(),
                ProcessId = process.Id,
                StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                ExecutablePath = directory.ExecutablePath,
                HasProcessGroup = true,
                StopEventName = stopEventName,
            }, cancellation.Token);

            // Act
            await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);

            // Assert
            LocalProcessRegistration? observed = await store.LoadAsync(model.Name, "host", cancellation.Token);
            observed.ShouldNotBeNull();
            observed.ProcessId.ShouldBe(process.Id);
            process.HasExited.ShouldBeFalse();
            gateway.ResourceStates.GetState(model.Resources[0].Id).ShouldBe(ResourceLifecycle.Running);
        }
        finally
        {
            try
            {
                await ((IApplicationGateway)gateway).StopAsync(CancellationToken.None);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
    }

    private static string StateDirectory(LongPathTestDirectory directory) =>
        Path.Combine(directory.HostDirectory, ".cohesion");

    private static LocalGateway CreateGateway(
        LongPathTestDirectory directory,
        Func<string, string?>? shortPathResolver = null) => new(new LocalGatewayOptions
        {
            BaseDirectory = directory.HostDirectory,
            StateDirectory = StateDirectory(directory),
            ProbeInterval = TimeSpan.FromMilliseconds(50),
            ProbeTimeout = TimeSpan.FromSeconds(5),
            ReadinessBudget = TimeSpan.FromSeconds(20),
            StopGrace = TimeSpan.FromSeconds(5),
        }, shortPathResolver ?? LocalProcessPath.TryGetShortPath);

    private static IApplicationModel CreateModel(
        LongPathTestDirectory directory,
        LocalGateway gateway,
        IReadOnlyDictionary<string, string>? environment = null,
        ResourceManifestProbe? readiness = null)
    {
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("long-path-tests"), []).UseGateway(gateway);
        builder.AddResource(new ResourceManifest
        {
            Name = "host",
            Application = "long-path-tests",
            Kind = "test",
            ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "Assimalign.Cohesion.ApplicationModel.Gateway.TestHost.dll",
                AppHost = directory.ExecutablePath,
            },
            Endpoints = [new ResourceManifestEndpoint
            {
                Name = "http", Scheme = "http", Protocol = "tcp", ContainerPort = 8080,
            }],
            ControlPlane = new ResourceManifestControlPlane { Endpoint = "http", Path = "/cohesion/v1" },
            Probes = new ResourceManifestProbes
            {
                Startup = new ResourceManifestProbe { None = true },
                Readiness = readiness ?? new ResourceManifestProbe { None = true },
                Liveness = new ResourceManifestProbe { None = true },
            },
            Mounts = [new ResourceManifestMount
            {
                Name = "data", Kind = ResourceMountKind.Volume, ContainerPath = "/data", Size = "1Gi",
            }],
            EnvironmentVariables = environment ?? new Dictionary<string, string>(),
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = WorkloadKind.StatefulSet, RestartPolicy = "Never", StopGraceSeconds = 5,
            },
        });
        return builder.Build().Model;
    }
}
