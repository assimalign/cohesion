using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

[Collection(LocalGatewayConsoleCollection.Name)]
public class LocalGatewayTests
{
    private const string ApplicationNameValue = "gateway-tests";
    private const string TestHostAssembly = "Assimalign.Cohesion.ApplicationModel.Gateway.TestHost";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: name is local")]
    public void Name_DefaultGateway_IsLocal()
    {
        new LocalGateway().Name.ShouldBe((ResourceName)"local");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: render emits a deterministic process unit without runtime state")]
    public async Task RunAsync_RenderMode_EmitsCompiledProcessWithoutStartingOrPersisting()
    {
        // Arrange
        string root = Directory.CreateTempSubdirectory("cohesion-local-render-").FullName;
        string launchMarker = Path.Combine(root, "launched");
        try
        {
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Z_LAST"] = "last",
                ["A_FIRST"] = "first",
                ["TEST_STARTED_PATH"] = launchMarker,
            };
            ResourceManifest manifest = CreateManifest(
                "rendered-service",
                environment,
                readiness: HttpProbe("/readyz"),
                startup: NoneProbe(),
                liveness: TcpProbe(),
                restartPolicy: "Always",
                stopGraceSeconds: 17);
            LocalGateway gateway = CreateGateway(root);
            IApplication application = BuildApplication(gateway, manifest, ["--mode=render"]);
            TextWriter original = Console.Out;
            using var output = new StringWriter(CultureInfo.InvariantCulture);

            try
            {
                Console.SetOut(output);

                // Act
                await application.RunAsync();

                // Assert
                using JsonDocument document = JsonDocument.Parse(output.ToString());
                JsonElement rootElement = document.RootElement;
                rootElement.GetProperty("schema").GetString().ShouldBe("cohesion/local-plan-set/v1");
                rootElement.GetProperty("gateway").GetString().ShouldBe("local");
                JsonElement unit = rootElement
                    .GetProperty("applications")[0]
                    .GetProperty("resources")[0]
                    .GetProperty("unit");
                unit.GetProperty("kind").GetString().ShouldBe("process");
                unit.GetProperty("artifact").GetProperty("identity").GetString().ShouldBe(TestHostPath);
                unit.GetProperty("restartPolicy").GetString().ShouldBe("Always");
                unit.GetProperty("stopGraceSeconds").GetInt32().ShouldBe(17);
                JsonElement endpoint = unit.GetProperty("endpoints")[0];
                endpoint.GetProperty("scheme").GetString().ShouldBe("http");
                endpoint.GetProperty("port").GetInt32().ShouldBe(0);
                endpoint.GetProperty("allocation").GetString().ShouldBe("persistent");
                endpoint.GetProperty("public").GetBoolean().ShouldBeTrue();
                unit.GetProperty("controlPlane").GetProperty("path").GetString()
                    .ShouldBe("/cohesion/v1");
                string rendered = output.ToString();
                rendered.IndexOf("A_FIRST", StringComparison.Ordinal)
                    .ShouldBeLessThan(rendered.IndexOf("Z_LAST", StringComparison.Ordinal));

                using var repeatedOutput = new StringWriter(CultureInfo.InvariantCulture);
                await ((IApplicationGatewayRenderer)gateway).RenderAsync(
                    [application.Model],
                    repeatedOutput);
                repeatedOutput.ToString().ShouldBe(rendered);

                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                using var canceledOutput = new StringWriter(CultureInfo.InvariantCulture);
                await Should.ThrowAsync<OperationCanceledException>(() =>
                    ((IApplicationGatewayRenderer)gateway).RenderAsync(
                        [application.Model],
                        canceledOutput,
                        cancellation.Token));
                canceledOutput.ToString().ShouldBeEmpty();
                File.Exists(launchMarker).ShouldBeFalse();
                Directory.Exists(Path.Combine(root, ".cohesion")).ShouldBeFalse();
            }
            finally
            {
                Console.SetOut(original);
            }
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - ProbeSpec: Http retains the validated URI and escaped endpoint path")]
    public void Http_EndpointUri_RetainsAddressAndEscapedPath()
    {
        // Arrange
        var address = new Uri("https://example.test:8443/health%20ready");

        // Act
        IProbeSpec probe = ProbeSpec.Http(address);

        // Assert
        probe.Address.ShouldBe(address);
        probe.Path.ShouldBe("/health%20ready");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - ProbeSpec: Http rejects non-HTTP endpoint schemes")]
    public void Http_NonHttpEndpoint_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() => ProbeSpec.Http(new Uri("tcp://x:1")));
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: restart policy preserves final sysexits")]
    [InlineData(RestartPolicy.OnFailure, 0, false)]
    [InlineData(RestartPolicy.OnFailure, 1, true)]
    [InlineData(RestartPolicy.OnFailure, 64, false)]
    [InlineData(RestartPolicy.OnFailure, 69, true)]
    [InlineData(RestartPolicy.OnFailure, 70, false)]
    [InlineData(RestartPolicy.OnFailure, 75, true)]
    [InlineData(RestartPolicy.OnFailure, 130, true)]
    [InlineData(RestartPolicy.OnFailure, 143, true)]
    [InlineData(RestartPolicy.OnFailure, -1, true)]
    [InlineData(RestartPolicy.OnFailure, 137, true)]
    [InlineData(RestartPolicy.OnFailure, -1073741510, true)]
    [InlineData(RestartPolicy.Always, 0, true)]
    [InlineData(RestartPolicy.Always, 64, false)]
    [InlineData(RestartPolicy.Always, 70, false)]
    [InlineData(RestartPolicy.Never, 75, false)]
    public void ShouldRestart_ExitCodeAndPolicy_ReturnExpected(
        RestartPolicy policy,
        int exitCode,
        bool expected)
    {
        LocalGatewayProcessSupervisor.ShouldRestart(policy, exitCode).ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: restart backoff doubles and caps at thirty seconds")]
    public void CalculateRestartBackoff_AttemptSequence_DoublesAndCaps()
    {
        TimeSpan[] actual = new TimeSpan[7];
        for (int attempt = 1; attempt <= actual.Length; attempt++)
        {
            actual[attempt - 1] = LocalGatewayProcessSupervisor.CalculateRestartBackoff(
                attempt,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(30));
        }

        actual.ShouldBe(
        [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(16),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30),
        ]);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: graceful stop signals child and removes pid")]
    public async Task RunAsync_GracefulStop_ChildObservesSignalWithinGrace()
    {
        string root = CreateTestDirectory();
        string observed = Path.Combine(root, "stop-observed.txt");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TEST_STOP_OBSERVED_PATH"] = observed,
        };
        ResourceManifest manifest = CreateManifest(
            "svc",
            environment,
            readiness: TcpProbe(),
            startup: NoneProbe(),
            liveness: NoneProbe(),
            stopGraceSeconds: 5);
        LocalGateway gateway = CreateGateway(root);
        IApplication application = BuildApplication(gateway, manifest);
        using var cancellation = new CancellationTokenSource();
        Task run = application.RunAsync(cancellation.Token);

        try
        {
            ResourceId resource = ResourceIdOf(manifest.Name);
            await WaitForStateAsync(gateway, resource, ResourceLifecycle.Running);
            string pidPath = ProcessPath(root, manifest.Name);
            int processId = ReadProcessId(pidPath);
            var stopwatch = Stopwatch.StartNew();

            cancellation.Cancel();
            await run.WaitAsync(TestTimeout);

            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
            await WaitForTextAsync(observed, "observed");
            gateway.ResourceStates.GetState(resource).ShouldBe(ResourceLifecycle.Stopped);
            File.Exists(pidPath).ShouldBeFalse();
            ProcessExists(processId).ShouldBeFalse();
            File.ReadAllText(Path.Combine(root, ".cohesion", ApplicationNameValue, ".state", "owner")).Trim()
                .ShouldBe($"{ApplicationNameValue}@local");
        }
        finally
        {
            if (!cancellation.IsCancellationRequested)
            {
                await StopApplicationAsync(cancellation, run);
            }

            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: cancellation during startup stops without uninstalling persistent state")]
    public async Task RunAsync_CanceledBeforeReadiness_StopsAndRetainsPortsAndVolume()
    {
        // Arrange
        string root = CreateTestDirectory();
        string observed = Path.Combine(root, "stop-observed.txt");
        string bound = Path.Combine(root, "bound.txt");
        string markerGate = Path.Combine(root, "ready-marker-gate");
        string status = Path.Combine(root, "exec.status");
        File.WriteAllText(status, "0", Encoding.UTF8);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TEST_STOP_OBSERVED_PATH"] = observed,
            ["TEST_BOUND_PATH"] = bound,
            ["TEST_READY_MARKER_GATE_PATH"] = markerGate,
        };
        ResourceManifest manifest = CreateManifest(
            "svc",
            environment,
            readiness: new ResourceManifestProbe
            {
                Exec = [TestHostPath, "exec-probe", status],
            },
            startup: NoneProbe(),
            liveness: NoneProbe(),
            mounts:
            [
                new ResourceManifestMount
                {
                    Name = "cache",
                    Kind = ResourceMountKind.Volume,
                    ContainerPath = "/cache",
                    Size = "1Gi",
                },
            ],
            stopGraceSeconds: 5) with
        {
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = WorkloadKind.StatefulSet,
                RestartPolicy = "Never",
                StopGraceSeconds = 5,
            },
        };
        LocalGateway gateway = CreateGateway(root);
        IApplication application = BuildApplication(gateway, manifest);
        using var cancellation = new CancellationTokenSource();
        Task run = application.RunAsync(cancellation.Token);

        try
        {
            await WaitForFileAsync(bound);
            string pidPath = ProcessPath(root, manifest.Name);
            int processId = ReadProcessId(pidPath);
            gateway.ResourceStates.GetState(ResourceIdOf(manifest.Name))
                .ShouldBe(ResourceLifecycle.Starting);

            // Act
            cancellation.Cancel();
            await run.WaitAsync(TestTimeout);

            // Assert
            await WaitForTextAsync(observed, "observed");
            gateway.ResourceStates.GetState(ResourceIdOf(manifest.Name))
                .ShouldBe(ResourceLifecycle.Stopped);
            File.Exists(pidPath).ShouldBeFalse();
            ProcessExists(processId).ShouldBeFalse();
            Directory.Exists(Path.Combine(
                root,
                ".cohesion",
                ApplicationNameValue,
                manifest.Name.ToString(),
                "cache")).ShouldBeTrue();
            using JsonDocument ports = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
                root,
                ".cohesion",
                ApplicationNameValue,
                ".state",
                "ports.json")));
            ports.RootElement
                .GetProperty("resources")
                .TryGetProperty(manifest.Name.ToString(), out _)
                .ShouldBeTrue();
            using LocalFileLease releasedLease = await new LocalProcessStateStore(
                    Path.Combine(root, ".cohesion"))
                .AcquireApplicationLeaseAsync(
                    ApplicationName.Parse(ApplicationNameValue),
                    $"{ApplicationNameValue}@local",
                    adopt: false,
                    CancellationToken.None);
        }
        finally
        {
            if (!cancellation.IsCancellationRequested)
            {
                await StopApplicationAsync(cancellation, run);
            }

            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: ignored graceful stop is force-killed and classified")]
    public async Task RunAsync_IgnoredGracefulStop_IsForcedAfterResourceGrace()
    {
        string root = CreateTestDirectory();
        string observed = Path.Combine(root, "stop-observed.txt");
        string descendant = Path.Combine(root, "descendant.txt");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TEST_STOP_OBSERVED_PATH"] = observed,
            ["TEST_IGNORE_STOP"] = "true",
            ["TEST_DESCENDANT_PID_PATH"] = descendant,
        };
        ResourceManifest manifest = CreateManifest(
            "svc",
            environment,
            readiness: TcpProbe(),
            startup: NoneProbe(),
            liveness: NoneProbe(),
            stopGraceSeconds: 5);
        LocalGateway gateway = CreateGateway(root);
        var forced = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ResourceStates.StateChanged += (_, args) =>
        {
            if (args.Resource == ResourceIdOf(manifest.Name)
                && args.Current == ResourceLifecycle.Failed
                && args.Detail?.Contains("Failed(forced)", StringComparison.Ordinal) is true)
            {
                forced.TrySetResult(args.Detail);
            }
        };
        IApplication application = BuildApplication(gateway, manifest);
        using var cancellation = new CancellationTokenSource();
        Task run = application.RunAsync(cancellation.Token);

        try
        {
            ResourceId resource = ResourceIdOf(manifest.Name);
            await WaitForStateAsync(gateway, resource, ResourceLifecycle.Running);
            string pidPath = ProcessPath(root, manifest.Name);
            int processId = ReadProcessId(pidPath);
            await WaitForFileAsync(descendant);
            int descendantProcessId = int.Parse(
                File.ReadAllText(descendant),
                NumberStyles.None,
                CultureInfo.InvariantCulture);
            var stopwatch = Stopwatch.StartNew();

            cancellation.Cancel();
            await run.WaitAsync(TestTimeout);
            string? detail = await forced.Task.WaitAsync(TestTimeout);

            stopwatch.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(4.5));
            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
            await WaitForTextAsync(observed, "observed");
            detail.ShouldNotBeNull();
            detail.ShouldContain("Failed(forced)");
            gateway.ResourceStates.GetState(resource).ShouldBe(ResourceLifecycle.Failed);
            File.Exists(pidPath).ShouldBeFalse();
            ProcessExists(processId).ShouldBeFalse();
            ProcessExists(descendantProcessId).ShouldBeFalse();
        }
        finally
        {
            if (!cancellation.IsCancellationRequested)
            {
                await StopApplicationAsync(cancellation, run);
            }

            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: live application lease excludes a second supervisor")]
    public async Task RunAsync_SecondGatewayWhileLeaseIsHeld_RefusesWithoutTouchingChild()
    {
        string root = CreateTestDirectory();
        string launchCount = Path.Combine(root, "launch-count.txt");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TEST_LAUNCH_COUNT_PATH"] = launchCount,
        };
        ResourceManifest manifest = CreateManifest(
            "svc",
            environment,
            readiness: TcpProbe(),
            startup: NoneProbe(),
            liveness: NoneProbe(),
            stopGraceSeconds: 5);
        LocalGateway firstGateway = CreateGateway(root);
        LocalGateway secondGateway = CreateGateway(root);
        IApplication firstApplication = BuildApplication(firstGateway, manifest);
        IApplication secondApplication = BuildApplication(secondGateway, manifest);
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        Task firstRun = firstApplication.RunAsync(firstCancellation.Token);

        try
        {
            await WaitForStateAsync(firstGateway, ResourceIdOf(manifest.Name), ResourceLifecycle.Running);
            string pidPath = ProcessPath(root, manifest.Name);
            int originalProcessId = ReadProcessId(pidPath);

            InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
                async () => await secondApplication
                    .RunAsync(secondCancellation.Token)
                    .WaitAsync(TestTimeout));

            exception.Message.ShouldContain("already supervised", Case.Sensitive);
            ReadProcessId(pidPath).ShouldBe(originalProcessId);
            File.ReadAllText(launchCount).Trim().ShouldBe("1");
            ProcessExists(originalProcessId).ShouldBeTrue();
        }
        finally
        {
            await StopApplicationAsync(firstCancellation, firstRun);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: released application lease permits verified orphan recovery")]
    public async Task RunAsync_VerifiedOrphanWithoutLease_ReattachesToChild()
    {
        string root = CreateTestDirectory();
        string launchCount = Path.Combine(root, "launch-count.txt");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TEST_LAUNCH_COUNT_PATH"] = launchCount,
        };
        ResourceManifest manifest = CreateManifest(
            "svc",
            environment,
            readiness: TcpProbe(),
            startup: NoneProbe(),
            liveness: NoneProbe(),
            stopGraceSeconds: 5);
        OrphanProcess orphan = await StartOrphanAsync(root, manifest);
        await WaitForTextAsync(launchCount, "1");
        LocalGateway gateway = CreateGateway(root);
        IApplication application = BuildApplication(gateway, manifest);
        using var cancellation = new CancellationTokenSource();
        Task run = application.RunAsync(cancellation.Token);

        try
        {
            await WaitForStateAsync(gateway, ResourceIdOf(manifest.Name), ResourceLifecycle.Running);

            ReadProcessId(ProcessPath(root, manifest.Name)).ShouldBe(orphan.Process.Id);
            File.ReadAllText(launchCount).Trim().ShouldBe("1");
        }
        finally
        {
            try
            {
                await StopApplicationAsync(cancellation, run);
            }
            finally
            {
                await DisposeOrphanAsync(orphan);
                DeleteTestDirectory(root);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: restart-orphans replaces verified child")]
    public async Task RunAsync_VerifiedOrphanWithRestartOrphans_ReplacesChild()
    {
        string root = CreateTestDirectory();
        string launchCount = Path.Combine(root, "launch-count.txt");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TEST_LAUNCH_COUNT_PATH"] = launchCount,
        };
        ResourceManifest manifest = CreateManifest(
            "svc",
            environment,
            readiness: TcpProbe(),
            startup: NoneProbe(),
            liveness: NoneProbe(),
            stopGraceSeconds: 5);
        OrphanProcess orphan = await StartOrphanAsync(root, manifest);
        await WaitForTextAsync(launchCount, "1");
        int originalProcessId = orphan.Process.Id;
        LocalGateway gateway = CreateGateway(root);
        IApplication application = BuildApplication(gateway, manifest, ["--restart-orphans"]);
        using var cancellation = new CancellationTokenSource();
        Task run = application.RunAsync(cancellation.Token);

        try
        {
            await WaitForStateAsync(gateway, ResourceIdOf(manifest.Name), ResourceLifecycle.Running);
            await WaitForTextAsync(launchCount, "2");

            ReadProcessId(ProcessPath(root, manifest.Name)).ShouldNotBe(originalProcessId);
            ProcessExists(originalProcessId).ShouldBeFalse();
        }
        finally
        {
            try
            {
                await StopApplicationAsync(cancellation, run);
            }
            finally
            {
                await DisposeOrphanAsync(orphan);
                DeleteTestDirectory(root);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: cancellation while replacing an orphan does not launch a successor")]
    public async Task RunAsync_CanceledWhileStoppingRestartOrphan_DoesNotLaunchReplacement()
    {
        // Arrange
        string root = CreateTestDirectory();
        string launchCount = Path.Combine(root, "launch-count.txt");
        string stopObserved = Path.Combine(root, "stop-observed.txt");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TEST_LAUNCH_COUNT_PATH"] = launchCount,
            ["TEST_STOP_OBSERVED_PATH"] = stopObserved,
            ["TEST_IGNORE_STOP"] = "true",
        };
        ResourceManifest manifest = CreateManifest(
            "svc",
            environment,
            readiness: TcpProbe(),
            startup: NoneProbe(),
            liveness: NoneProbe(),
            stopGraceSeconds: 1);
        OrphanProcess orphan = await StartOrphanAsync(root, manifest);
        await WaitForTextAsync(launchCount, "1");
        LocalGateway gateway = CreateGateway(root);
        IApplication application = BuildApplication(gateway, manifest, ["--restart-orphans"]);
        using var cancellation = new CancellationTokenSource();
        Task run = application.RunAsync(cancellation.Token);

        try
        {
            await WaitForTextAsync(stopObserved, "observed");

            // Act
            cancellation.Cancel();
            await run.WaitAsync(TestTimeout);

            // Assert
            File.ReadAllText(launchCount).Trim().ShouldBe("1");
            ProcessExists(orphan.Process.Id).ShouldBeFalse();
            File.Exists(ProcessPath(root, manifest.Name)).ShouldBeFalse();
            HasPortAllocation(root, manifest.Name).ShouldBeTrue();
        }
        finally
        {
            try
            {
                if (!cancellation.IsCancellationRequested)
                {
                    await StopApplicationAsync(cancellation, run);
                }
            }
            finally
            {
                await DisposeOrphanAsync(orphan);
                DeleteTestDirectory(root);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: concurrent stop and delete share teardown")]
    public async Task StopAsync_DeleteWhileStopIsInProgress_WaitsForSharedTeardown()
    {
        // Arrange
        string root = CreateTestDirectory();
        string exitGate = Path.Combine(root, "exit-gate");
        ResourceManifest manifest = CreateManifest(
            "svc",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TEST_EXIT_GATE_PATH"] = exitGate,
            },
            readiness: TcpProbe(),
            startup: NoneProbe(),
            liveness: NoneProbe(),
            stopGraceSeconds: 5);
        IApplication application = BuildApplication(CreateGateway(root), manifest);
        IApplicationModel model = application.Model;
        IApplicationResourceDescriptor descriptor = model.Descriptors[0];
        string stateDirectory = Path.Combine(root, ".cohesion");
        var options = new LocalGatewayOptions
        {
            BaseDirectory = AppContext.BaseDirectory,
            StateDirectory = stateDirectory,
            ProbeInterval = TimeSpan.FromMilliseconds(50),
            ProbeTimeout = TimeSpan.FromSeconds(2),
            ReadinessBudget = TimeSpan.FromSeconds(10),
            InitialRestartBackoff = TimeSpan.FromMilliseconds(100),
            MaximumRestartBackoff = TimeSpan.FromSeconds(1),
            StopGrace = TimeSpan.FromSeconds(5),
        };
        var state = new InMemoryResourceStateManager();
        var ports = new LocalPortStore(stateDirectory);
        var processState = new LocalProcessStateStore(stateDirectory);
        var supervisor = new LocalGatewayProcessSupervisor(options, processState);
        var preparer = new LocalResourcePreparer(
            ports,
            new LocalMountMaterializer(stateDirectory),
            options);
        var controller = new LocalPlanController(options, preparer, supervisor);
        var context = new ResourceControlContext(
            descriptor,
            model,
            state,
            Array.Empty<IApplicationResource>(),
            new ExecutableArtifact(descriptor.Resource.Id, TestHostPath),
            Array.Empty<ResourceDependencyObservation>());
        using var stopEntered = new ManualResetEventSlim();
        using var releaseStop = new ManualResetEventSlim();
        int blockStop = 0;
        state.StateChanged += (_, args) =>
        {
            if (args.Current == ResourceLifecycle.Stopping
                && Interlocked.Exchange(ref blockStop, 1) == 0)
            {
                stopEntered.Set();
                if (!releaseStop.Wait(TestTimeout))
                {
                    throw new TimeoutException("The test did not release the blocked stop transition.");
                }
            }
        };
        bool initialized = false;

        try
        {
            await supervisor.InitializeAsync([model], CancellationToken.None);
            initialized = true;
            await controller.ReconcileAsync(context, CancellationToken.None);
            ResourceLifecycle running = await state.WaitForStateAsync(
                descriptor.Resource.Id,
                new HashSet<ResourceLifecycle> { ResourceLifecycle.Running },
                TestTimeout);
            running.ShouldBe(ResourceLifecycle.Running);
            File.WriteAllText(exitGate, string.Empty);
            ResourceLifecycle stopped = await state.WaitForStateAsync(
                descriptor.Resource.Id,
                new HashSet<ResourceLifecycle> { ResourceLifecycle.Stopped },
                TestTimeout);
            stopped.ShouldBe(ResourceLifecycle.Stopped);
            HasPortAllocation(root, manifest.Name).ShouldBeTrue();

            Task stop = Task.Run(() => controller.StopAsync(context, CancellationToken.None));
            stopEntered.Wait(TestTimeout).ShouldBeTrue();

            // Act
            Task delete = controller.DeleteAsync(context, CancellationToken.None);

            // Assert
            delete.IsCompleted.ShouldBeFalse();
            HasPortAllocation(root, manifest.Name).ShouldBeTrue();
            releaseStop.Set();
            await Task.WhenAll(stop, delete).WaitAsync(TestTimeout);
            HasPortAllocation(root, manifest.Name).ShouldBeFalse();
        }
        finally
        {
            releaseStop.Set();
            if (initialized)
            {
                try
                {
                    await controller.StopAsync(context, CancellationToken.None);
                }
                finally
                {
                    await supervisor.ShutdownAsync(CancellationToken.None);
                }
            }

            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: direct plain executable is rejected")]
    public async Task RunAsync_DirectPlainExecutable_RequiresAddExecutable()
    {
        IApplicationBuilder builder = Assimalign.Cohesion.ApplicationModel.Application
            .CreateBuilder(ApplicationName.Parse(ApplicationNameValue), [])
            .UseLocalGateway();
        builder.AddResource(new TestExecutableResource("svc", "does-not-matter"));
        IApplication application = builder.Build();

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            async () => await application.RunAsync().WaitAsync(TestTimeout));

        exception.Message.ShouldContain("AddExecutable");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: ports persist across gateway instances")]
    public async Task RunAsync_TwoGatewayInstances_ReusesAllocatedPortAndInjectsContract()
    {
        string root = CreateTestDirectory();
        try
        {
            string firstCapture = Path.Combine(root, "first-env.json");
            string secondCapture = Path.Combine(root, "second-env.json");

            int firstPort = await RunOnceAndGetPortAsync(root, firstCapture);
            int secondPort = await RunOnceAndGetPortAsync(root, secondCapture);

            firstPort.ShouldBe(secondPort);
            File.Exists(Path.Combine(root, ".cohesion", ApplicationNameValue, ".state", "ports.json")).ShouldBeTrue();

            IReadOnlyDictionary<string, string> environment = ReadStringMap(secondCapture);
            environment[ResourceEnvironment.Application].ShouldBe(ApplicationNameValue);
            environment[ResourceEnvironment.Resource].ShouldBe("svc");
            environment[ResourceEnvironment.Gateway].ShouldBe("local");
            environment[ResourceEnvironment.Endpoint("http", "HOST")].ShouldBe("127.0.0.1");
            environment[ResourceEnvironment.Endpoint("http", "PORT")]
                .ShouldBe(firstPort.ToString(CultureInfo.InvariantCulture));
            environment[ResourceEnvironment.Endpoint("http", "SCHEME")].ShouldBe("http");
            environment[ResourceEnvironment.Endpoint("http", "PUBLIC_URL")]
                .ShouldBe($"http://127.0.0.1:{firstPort}");
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: default control-plane probes use role paths")]
    public async Task RunAsync_DefaultControlPlaneProbes_UseRolePaths()
    {
        string root = CreateTestDirectory();
        string status = Path.Combine(root, "ready.status");
        string requests = Path.Combine(root, "requests.log");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TEST_READY_STATUS_PATH"] = status,
            ["TEST_REQUEST_LOG_PATH"] = requests,
            ["TEST_REQUIRE_CONTROL_PLANE_BEARER"] = "true",
        };
        ResourceManifest manifest = CreateManifest(
            "svc",
            environment,
            readiness: null,
            startup: null,
            liveness: null);
        LocalGateway gateway = CreateGateway(root);
        IApplication application = BuildApplication(gateway, manifest);
        using var cancellation = new CancellationTokenSource();
        Task run = application.RunAsync(cancellation.Token);

        try
        {
            await WaitForFileAsync(requests);
            gateway.ResourceStates.GetState(ResourceIdOf(manifest.Name)).ShouldBe(ResourceLifecycle.Starting);

            File.WriteAllText(status, "200", Encoding.UTF8);
            await WaitForStateAsync(gateway, ResourceIdOf(manifest.Name), ResourceLifecycle.Running);
            try
            {
                await WaitForConditionAsync(
                    () => CountLinesContaining(requests, "GET /cohesion/v1/livez") > 0,
                    "The default control-plane liveness route was not probed.");
            }
            catch (TimeoutException exception)
            {
                ResourceLifecycle state = gateway.ResourceStates.GetState(ResourceIdOf(manifest.Name));
                string diagnosticLog = File.Exists(requests) ? File.ReadAllText(requests) : "<missing>";
                throw new TimeoutException(
                    $"{exception.Message} State: {state}. Requests: {diagnosticLog}",
                    exception);
            }

            string requestLog = File.ReadAllText(requests);
            requestLog.ShouldContain("GET /cohesion/v1/readyz");
            requestLog.ShouldContain("GET /cohesion/v1/livez");
        }
        finally
        {
            await StopApplicationAsync(cancellation, run);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: HTTP 404 fails readiness immediately")]
    public async Task RunAsync_HttpProbeReturns404_FailsFastWithActionableDetail()
    {
        string root = CreateTestDirectory();
        string status = Path.Combine(root, "ready.status");
        File.WriteAllText(status, "404", Encoding.UTF8);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TEST_READY_STATUS_PATH"] = status,
        };
        ResourceManifest manifest = CreateManifest(
            "svc",
            environment,
            readiness: HttpProbe("/readyz"),
            startup: NoneProbe(),
            liveness: NoneProbe());
        LocalGateway gateway = CreateGateway(root, options => options.ReadinessBudget = TimeSpan.FromSeconds(10));
        var failed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ResourceStates.StateChanged += (_, args) =>
        {
            if (args.Resource == ResourceIdOf(manifest.Name) && args.Current == ResourceLifecycle.Failed)
            {
                failed.TrySetResult(args.Detail);
            }
        };
        IApplication application = BuildApplication(gateway, manifest);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
                async () => await application.RunAsync().WaitAsync(TestTimeout));
            string? detail = await failed.Task.WaitAsync(TestTimeout);

            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
            exception.Message.ShouldContain("did not reach Running");
            detail.ShouldNotBeNull();
            detail.ShouldContain("404");
            detail.ShouldContain("/readyz");
            detail.ShouldContain("Verify");
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: TCP readiness probe reaches Running")]
    public async Task RunAsync_TcpReadinessProbe_ReachesRunning()
    {
        string root = CreateTestDirectory();
        ResourceManifest manifest = CreateManifest(
            "svc",
            environment: null,
            readiness: TcpProbe(),
            startup: NoneProbe(),
            liveness: NoneProbe());

        await RunAndAssertRunningAsync(root, manifest);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: ready line starts the exec readiness probe")]
    public async Task RunAsync_ReadyLine_StartsExecReadinessProbe()
    {
        string root = CreateTestDirectory();
        string status = Path.Combine(root, "exec.status");
        string markerGate = Path.Combine(root, "marker.gate");
        string bound = Path.Combine(root, "bound.txt");
        string probeCapture = Path.Combine(root, "probe.txt");
        File.WriteAllText(status, "0", Encoding.UTF8);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TEST_READY_MARKER_GATE_PATH"] = markerGate,
            ["TEST_BOUND_PATH"] = bound,
            ["TEST_EXEC_PROBE_CAPTURE_PATH"] = probeCapture,
        };
        ResourceManifest manifest = CreateManifest(
            "svc",
            environment,
            readiness: new ResourceManifestProbe
            {
                Exec = new[] { TestHostPath, "exec-probe", status },
            },
            startup: NoneProbe(),
            liveness: NoneProbe());
        LocalGateway gateway = CreateGateway(root);
        IApplication application = BuildApplication(gateway, manifest);
        using var cancellation = new CancellationTokenSource();
        Task run = application.RunAsync(cancellation.Token);

        try
        {
            await WaitForFileAsync(bound);
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            File.Exists(probeCapture).ShouldBeFalse();
            gateway.ResourceStates.GetState(ResourceIdOf(manifest.Name)).ShouldBe(ResourceLifecycle.Starting);

            File.WriteAllText(markerGate, string.Empty, Encoding.UTF8);
            await WaitForFileAsync(probeCapture);
            await WaitForStateAsync(gateway, ResourceIdOf(manifest.Name), ResourceLifecycle.Running);
        }
        finally
        {
            await StopApplicationAsync(cancellation, run);
            DeleteTestDirectory(root);
        }
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: liveness degradation restarts with backoff")]
    [InlineData("OnFailure")]
    [InlineData("Always")]
    public async Task RunAsync_ThreeLivenessFailures_DegradesAndRestartsWithBackoff(
        string restartPolicy)
    {
        string root = CreateTestDirectory();
        string liveStatus = Path.Combine(root, "live.status");
        string launchCount = Path.Combine(root, "launch-count.txt");
        File.WriteAllText(liveStatus, "200", Encoding.UTF8);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TEST_LIVE_STATUS_PATH"] = liveStatus,
            ["TEST_LAUNCH_COUNT_PATH"] = launchCount,
        };
        ResourceManifest manifest = CreateManifest(
            "svc",
            environment,
            readiness: HttpProbe("/readyz"),
            startup: NoneProbe(),
            liveness: HttpProbe("/livez"),
            restartPolicy: restartPolicy);
        LocalGateway gateway = CreateGateway(root, options =>
        {
            options.ProbeInterval = TimeSpan.FromMilliseconds(100);
            options.InitialRestartBackoff = TimeSpan.FromMilliseconds(200);
            options.MaximumRestartBackoff = TimeSpan.FromMilliseconds(200);
        });
        var transitions = new ConcurrentQueue<StateObservation>();
        int runningCount = 0;
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ResourceStates.StateChanged += (_, args) =>
        {
            if (args.Resource != ResourceIdOf(manifest.Name))
            {
                return;
            }

            transitions.Enqueue(new StateObservation(args.Current, args.Detail, Stopwatch.GetTimestamp()));
            if (args.Current == ResourceLifecycle.Running
                && Interlocked.Increment(ref runningCount) == 2)
            {
                restarted.TrySetResult();
            }
        };
        IApplication application = BuildApplication(gateway, manifest);
        using var cancellation = new CancellationTokenSource();
        Task run = application.RunAsync(cancellation.Token);

        try
        {
            await WaitForStateAsync(gateway, ResourceIdOf(manifest.Name), ResourceLifecycle.Running);
            File.WriteAllText(liveStatus, "500", Encoding.UTF8);
            await WaitForTextAsync(launchCount, "2");
            File.WriteAllText(liveStatus, "200", Encoding.UTF8);
            await restarted.Task.WaitAsync(TestTimeout);

            StateObservation[] observed = transitions.ToArray();
            int degraded = IndexOf(observed, ResourceLifecycle.Degraded);
            int stopping = IndexOf(observed, ResourceLifecycle.Stopping, degraded + 1);
            int starting = IndexOf(observed, ResourceLifecycle.Starting, stopping + 1);
            int running = IndexOf(observed, ResourceLifecycle.Running, starting + 1);

            degraded.ShouldBeGreaterThanOrEqualTo(0);
            stopping.ShouldBeGreaterThan(degraded);
            starting.ShouldBeGreaterThan(stopping);
            running.ShouldBeGreaterThan(starting);
            string? degradedDetail = observed[degraded].Detail;
            degradedDetail.ShouldNotBeNull();
            degradedDetail!.ShouldContain("500");

            TimeSpan failureWindow = Stopwatch.GetElapsedTime(
                observed[degraded].Timestamp,
                observed[stopping].Timestamp);
            failureWindow.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150));

            TimeSpan measuredBackoff = Stopwatch.GetElapsedTime(
                observed[stopping].Timestamp,
                observed[starting].Timestamp);
            measuredBackoff.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150));
        }
        finally
        {
            await StopApplicationAsync(cancellation, run);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: Never policy keeps a failed liveness probe Degraded")]
    public async Task RunAsync_LivenessFailsWithNeverPolicy_DoesNotRestartAndCanRecover()
    {
        string root = CreateTestDirectory();
        string liveStatus = Path.Combine(root, "live.status");
        string launchCount = Path.Combine(root, "launch-count.txt");
        string requests = Path.Combine(root, "requests.log");
        File.WriteAllText(liveStatus, "200", Encoding.UTF8);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TEST_LIVE_STATUS_PATH"] = liveStatus,
            ["TEST_LAUNCH_COUNT_PATH"] = launchCount,
            ["TEST_REQUEST_LOG_PATH"] = requests,
        };
        ResourceManifest manifest = CreateManifest(
            "svc",
            environment,
            readiness: HttpProbe("/readyz"),
            startup: NoneProbe(),
            liveness: HttpProbe("/livez"),
            restartPolicy: "Never");
        LocalGateway gateway = CreateGateway(root, options =>
            options.ProbeInterval = TimeSpan.FromMilliseconds(75));
        IApplication application = BuildApplication(gateway, manifest);
        using var cancellation = new CancellationTokenSource();
        Task run = application.RunAsync(cancellation.Token);

        try
        {
            ResourceId resource = ResourceIdOf(manifest.Name);
            await WaitForStateAsync(gateway, resource, ResourceLifecycle.Running);
            int healthyRequests = CountLinesContaining(requests, "GET /livez");
            File.WriteAllText(liveStatus, "500", Encoding.UTF8);

            await WaitForStateAsync(gateway, resource, ResourceLifecycle.Degraded);
            await WaitForConditionAsync(
                () => CountLinesContaining(requests, "GET /livez") >= healthyRequests + 4,
                "The Never-policy liveness probe did not continue after becoming Degraded.");

            File.ReadAllText(launchCount).Trim().ShouldBe("1");
            gateway.ResourceStates.GetState(resource).ShouldBe(ResourceLifecycle.Degraded);

            File.WriteAllText(liveStatus, "200", Encoding.UTF8);
            await WaitForStateAsync(gateway, resource, ResourceLifecycle.Running);
            File.ReadAllText(launchCount).Trim().ShouldBe("1");
        }
        finally
        {
            await StopApplicationAsync(cancellation, run);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: mounts materialize and inject paths")]
    public async Task RunAsync_ConfigurationMount_MaterializesAndInjectsPath()
    {
        string root = CreateTestDirectory();
        string capture = Path.Combine(root, "mounts.json");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TEST_MOUNT_CAPTURE_PATH"] = capture,
            ["TEST_MOUNT_NAMES"] = "settings",
        };
        ResourceManifest manifest = CreateManifest(
            "svc",
            environment,
            readiness: TcpProbe(),
            startup: NoneProbe(),
            liveness: NoneProbe(),
            mounts: new[]
            {
                new ResourceManifestMount
                {
                    Name = "settings",
                    Kind = ResourceMountKind.Configuration,
                    ContainerPath = "/settings.json",
                    Source = "literal:mount-value",
                },
            });
        LocalGateway gateway = CreateGateway(root);
        IApplication application = BuildApplication(gateway, manifest);
        using var cancellation = new CancellationTokenSource();
        Task run = application.RunAsync(cancellation.Token);

        try
        {
            await WaitForStateAsync(gateway, ResourceIdOf(manifest.Name), ResourceLifecycle.Running);
            await WaitForFileAsync(capture);

            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(capture));
            JsonElement mount = document.RootElement.GetProperty(ResourceEnvironment.Mount("settings"));
            string path = mount.GetProperty("path").GetString()!;
            byte[] content = mount.GetProperty("content").GetBytesFromBase64();
            path.ShouldBe(Path.Combine(root, ".cohesion", ApplicationNameValue, "svc", "settings"));
            File.Exists(path).ShouldBeTrue();

            if (OperatingSystem.IsWindows())
            {
                content.ShouldNotBe(Encoding.UTF8.GetBytes("mount-value"));
                Encoding.UTF8.GetString(content).ShouldNotContain("mount-value");
                Encoding.UTF8.GetString(
                    new Assimalign.Cohesion.Hosting.Resources.ResourceMount(path).ReadAllBytes())
                    .ShouldBe("mount-value");
            }
            else
            {
                Encoding.UTF8.GetString(content).ShouldBe("mount-value");
                File.GetUnixFileMode(path).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            await StopApplicationAsync(cancellation, run);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: child output receives resource prefix")]
    public async Task RunAsync_ChildOutput_PrefixesStdoutAndStderr()
    {
        string root = CreateTestDirectory();
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        Console.SetOut(stdout);
        Console.SetError(stderr);

        try
        {
            ResourceManifest manifest = CreateManifest(
                "svc",
                environment: null,
                readiness: TcpProbe(),
                startup: NoneProbe(),
                liveness: NoneProbe());
            await RunAndAssertRunningAsync(root, manifest);

            stdout.ToString().ShouldContain("[svc] test-host stdout");
            stderr.ToString().ShouldContain("[svc] test-host stderr");
            stdout.ToString().ShouldNotContain("[svc] svc: GenericPlanner");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: AddExecutable launches on its ready marker")]
    public async Task RunAsync_AddExecutableReadyMarker_IsOnlyReadinessSignal()
    {
        string root = CreateTestDirectory();
        string markerGate = Path.Combine(root, "emit-marker");
        string bound = Path.Combine(root, "bound.txt");
        LocalGateway gateway = CreateGateway(root);
        IApplicationBuilder builder = Assimalign.Cohesion.ApplicationModel.Application
            .CreateBuilder(ApplicationName.Parse(ApplicationNameValue), [])
            .UseGateway(gateway);
        IApplicationResourceDescriptor descriptor = builder.AddExecutable(
            "opaque",
            TestHostPath,
            options => options
                .UseReadyMarker("opaque-ready")
                .AddEndpoint(new ResourceEndpoint("http", "http", 0))
                .AddEnvironment("TEST_READY_MARKER", "opaque-ready")
                .AddEnvironment("TEST_READY_MARKER_GATE_PATH", markerGate)
                .AddEnvironment("TEST_BOUND_PATH", bound));
        IApplication application = builder.Build();
        using var cancellation = new CancellationTokenSource();
        Task run = application.RunAsync(cancellation.Token);

        try
        {
            await WaitForFileAsync(bound);
            gateway.ResourceStates.GetState(descriptor.Resource.Id).ShouldBe(ResourceLifecycle.Starting);

            File.WriteAllText(markerGate, string.Empty, Encoding.UTF8);
            await WaitForStateAsync(gateway, descriptor.Resource.Id, ResourceLifecycle.Running);
        }
        finally
        {
            await StopApplicationAsync(cancellation, run);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: observed dependencies gate launch and inject allocated addresses")]
    public async Task RunAsync_ManifestDependency_WaitsForRunningAndInjectsObservedEndpoint()
    {
        // Arrange
        string root = CreateTestDirectory();
        string markerGate = Path.Combine(root, "provider-ready");
        string providerBound = Path.Combine(root, "provider-bound.txt");
        string consumerCapture = Path.Combine(root, "consumer-env.json");
        const string dependencyName = "catalog-db";
        const string endpointName = "sql.main";
        string[] dependencyVariables = DependencyVariables(dependencyName, endpointName);
        ResourceManifest provider = CreateManifest(
            dependencyName,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TEST_ENDPOINT_NAME"] = endpointName,
                ["TEST_READY_MARKER_GATE_PATH"] = markerGate,
                ["TEST_BOUND_PATH"] = providerBound,
            },
            readiness: NoneProbe(),
            startup: NoneProbe(),
            liveness: NoneProbe()) with
        {
            Endpoints = [CreateEndpoint(endpointName, 5432)],
            ControlPlane = CreateControlPlane(endpointName),
        };
        ResourceManifest consumer = CreateManifest(
            "orders-api",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TEST_ENV_CAPTURE_PATH"] = consumerCapture,
                ["TEST_CAPTURE_ENV_NAMES"] = string.Join(';', dependencyVariables),
            },
            readiness: TcpProbe(),
            startup: NoneProbe(),
            liveness: NoneProbe()) with
        {
            References = [CreateReference(dependencyName, endpointName)],
        };
        LocalGateway gateway = CreateGateway(root);
        IApplication application = BuildApplication(gateway, [consumer, provider]);
        using var cancellation = new CancellationTokenSource();

        // Act
        Task run = application.RunAsync(cancellation.Token);

        try
        {
            await WaitForFileAsync(providerBound);

            // Assert
            gateway.ResourceStates.GetState(ResourceIdOf(provider.Name)).ShouldBe(ResourceLifecycle.Starting);
            File.Exists(consumerCapture).ShouldBeFalse();

            File.WriteAllText(markerGate, string.Empty, Encoding.UTF8);
            await WaitForStateAsync(gateway, ResourceIdOf(provider.Name), ResourceLifecycle.Running);
            await WaitForStateAsync(gateway, ResourceIdOf(consumer.Name), ResourceLifecycle.Running);
            await WaitForFileAsync(consumerCapture);

            ResourceEndpoint observed = gateway.ResourceStates
                .GetObservedEndpoints(ResourceIdOf(provider.Name))
                .ShouldHaveSingleItem();
            IReadOnlyDictionary<string, string> environment = ReadStringMap(consumerCapture);
            Uri address = Uri.CreateEndpoint(observed.Scheme, observed.Host!, observed.Port);

            observed.Port.ShouldNotBe(5432);
            environment[dependencyVariables[0]].ShouldBe(address.ToEndpointString());
            environment[dependencyVariables[1]].ShouldBe(address.IdnHost);
            environment[dependencyVariables[2]].ShouldBe(observed.Port.ToString(CultureInfo.InvariantCulture));
            environment[dependencyVariables[3]].ShouldBe(address.Scheme);
        }
        finally
        {
            await StopApplicationAsync(cancellation, run);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: optional absent references inject no dependency environment")]
    public async Task RunAsync_OptionalAbsentReference_InjectsNothing()
    {
        // Arrange
        string root = CreateTestDirectory();
        string capture = Path.Combine(root, "optional-env.json");
        string[] dependencyVariables = DependencyVariables("metrics-store", "http");
        ResourceManifest consumer = CreateManifest(
            "orders-api",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TEST_ENV_CAPTURE_PATH"] = capture,
                ["TEST_CAPTURE_ENV_NAMES"] = string.Join(';', dependencyVariables),
                [dependencyVariables[0]] = "http://declared.invalid:1234",
            },
            readiness: TcpProbe(),
            startup: NoneProbe(),
            liveness: NoneProbe()) with
        {
            References = [CreateReference("metrics-store", "http", optional: true)],
        };
        LocalGateway gateway = CreateGateway(root);
        IApplication application = BuildApplication(gateway, consumer);
        using var cancellation = new CancellationTokenSource();

        // Act
        Task run = application.RunAsync(cancellation.Token);

        try
        {
            await WaitForStateAsync(gateway, ResourceIdOf(consumer.Name), ResourceLifecycle.Running);
            await WaitForFileAsync(capture);
            IReadOnlyDictionary<string, string> environment = ReadStringMap(capture);

            // Assert
            foreach (string variable in dependencyVariables)
            {
                environment.ContainsKey(variable).ShouldBeFalse();
            }
        }
        finally
        {
            await StopApplicationAsync(cancellation, run);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: optional present references do not gate launch")]
    public async Task RunAsync_OptionalPresentReference_DoesNotGateOrInjectBeforeRunning()
    {
        // Arrange
        string root = CreateTestDirectory();
        string capture = Path.Combine(root, "optional-present-env.json");
        string markerGate = Path.Combine(root, "optional-provider-ready");
        const string dependencyName = "metrics-store";
        string[] dependencyVariables = DependencyVariables(dependencyName, "http");
        ResourceManifest consumer = CreateManifest(
            "orders-api",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TEST_ENV_CAPTURE_PATH"] = capture,
                ["TEST_CAPTURE_ENV_NAMES"] = string.Join(';', dependencyVariables),
            },
            readiness: TcpProbe(),
            startup: NoneProbe(),
            liveness: NoneProbe()) with
        {
            References = [CreateReference(dependencyName, "http", optional: true)],
        };
        ResourceManifest provider = CreateManifest(
            dependencyName,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TEST_READY_MARKER_GATE_PATH"] = markerGate,
            },
            readiness: NoneProbe(),
            startup: NoneProbe(),
            liveness: NoneProbe());
        LocalGateway gateway = CreateGateway(root);
        IApplication application = BuildApplication(gateway, [consumer, provider]);
        using var cancellation = new CancellationTokenSource();

        // Act
        Task run = application.RunAsync(cancellation.Token);

        try
        {
            await WaitForStateAsync(gateway, ResourceIdOf(consumer.Name), ResourceLifecycle.Running);
            await WaitForFileAsync(capture);
            IReadOnlyDictionary<string, string> environment = ReadStringMap(capture);

            // Assert
            gateway.ResourceStates.GetState(ResourceIdOf(provider.Name)).ShouldNotBe(ResourceLifecycle.Running);
            foreach (string variable in dependencyVariables)
            {
                environment.ContainsKey(variable).ShouldBeFalse();
            }

            File.WriteAllText(markerGate, string.Empty, Encoding.UTF8);
            await WaitForStateAsync(gateway, ResourceIdOf(provider.Name), ResourceLifecycle.Running);
        }
        finally
        {
            await StopApplicationAsync(cancellation, run);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: composite re-exports use outer dependency and mount names")]
    public async Task RunAsync_CompositeReExports_UseOuterEnvironmentNames()
    {
        // Arrange
        string root = CreateTestDirectory();
        string dependencyCapture = Path.Combine(root, "composite-dependency.json");
        string compositeEnvironmentCapture = Path.Combine(root, "composite-environment.json");
        string mountCapture = Path.Combine(root, "composite-mount.json");
        const string compositeName = "acme-core";
        const string endpointName = "database-db";
        const string mountName = "database-data";
        string[] dependencyVariables = DependencyVariables(compositeName, endpointName);
        string outerMountName = $"{compositeName}-{mountName}";
        string outerMountVariable = ResourceEnvironment.Mount(outerMountName);
        string unqualifiedMountVariable = ResourceEnvironment.Mount(mountName);
        ResourceManifest composite = CreateManifest(
            compositeName,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TEST_ENDPOINT_NAME"] = endpointName,
                ["TEST_ENV_CAPTURE_PATH"] = compositeEnvironmentCapture,
                ["TEST_CAPTURE_ENV_NAMES"] = $"{outerMountVariable};{unqualifiedMountVariable}",
                ["TEST_MOUNT_CAPTURE_PATH"] = mountCapture,
                ["TEST_MOUNT_NAMES"] = outerMountName,
                [unqualifiedMountVariable] = Path.Combine(root, "spoofed-composite-mount"),
            },
            readiness: NoneProbe(),
            startup: NoneProbe(),
            liveness: NoneProbe()) with
        {
            Kind = "Composite",
            Endpoints = [CreateEndpoint(endpointName, 5432)],
            ControlPlane = CreateControlPlane(endpointName),
            Mounts =
            [
                new ResourceManifestMount
                {
                    Name = mountName,
                    Kind = ResourceMountKind.Volume,
                    ContainerPath = "/database/data",
                    Size = "1Gi",
                },
            ],
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = WorkloadKind.StatefulSet,
                RestartPolicy = "Never",
            },
        };
        ResourceManifest consumer = CreateManifest(
            "acme-api",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TEST_ENV_CAPTURE_PATH"] = dependencyCapture,
                ["TEST_CAPTURE_ENV_NAMES"] = string.Join(';', dependencyVariables),
            },
            readiness: TcpProbe(),
            startup: NoneProbe(),
            liveness: NoneProbe()) with
        {
            References = [CreateReference(compositeName, endpointName)],
        };
        LocalGateway gateway = CreateGateway(root);
        IApplication application = BuildApplication(gateway, [consumer, composite]);
        using var cancellation = new CancellationTokenSource();

        // Act
        Task run = application.RunAsync(cancellation.Token);

        try
        {
            await WaitForStateAsync(gateway, ResourceIdOf(composite.Name), ResourceLifecycle.Running);
            await WaitForStateAsync(gateway, ResourceIdOf(consumer.Name), ResourceLifecycle.Running);
            await WaitForFileAsync(dependencyCapture);
            await WaitForFileAsync(compositeEnvironmentCapture);
            await WaitForFileAsync(mountCapture);

            // Assert
            IReadOnlyDictionary<string, string> dependencyEnvironment = ReadStringMap(dependencyCapture);
            foreach (string variable in dependencyVariables)
            {
                dependencyEnvironment.ContainsKey(variable).ShouldBeTrue();
            }

            ResourceEndpoint observed = gateway.ResourceStates
                .GetObservedEndpoints(ResourceIdOf(composite.Name))
                .ShouldHaveSingleItem();
            Uri address = Uri.CreateEndpoint(observed.Scheme, observed.Host!, observed.Port);
            dependencyEnvironment[dependencyVariables[0]].ShouldBe(address.ToEndpointString());
            dependencyEnvironment[dependencyVariables[1]].ShouldBe(address.IdnHost);
            dependencyEnvironment[dependencyVariables[2]].ShouldBe(
                address.Port.ToString(CultureInfo.InvariantCulture));
            dependencyEnvironment[dependencyVariables[3]].ShouldBe(address.Scheme);

            IReadOnlyDictionary<string, string> compositeEnvironment = ReadStringMap(compositeEnvironmentCapture);
            compositeEnvironment.ContainsKey(outerMountVariable).ShouldBeTrue();
            compositeEnvironment.ContainsKey(unqualifiedMountVariable).ShouldBeFalse();

            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(mountCapture));
            JsonElement mount = document.RootElement.GetProperty(outerMountVariable);
            mount.GetProperty("kind").GetString().ShouldBe("directory");
            mount.GetProperty("path").GetString().ShouldBe(
                Path.Combine(root, ".cohesion", ApplicationNameValue, compositeName, mountName));
        }
        finally
        {
            await StopApplicationAsync(cancellation, run);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local gateway: volume and secret mounts inject materialized paths")]
    public async Task RunAsync_VolumeAndSecretMounts_InjectMaterializedPaths()
    {
        // Arrange
        string root = CreateTestDirectory();
        string capture = Path.Combine(root, "mount-kinds.json");
        const string volumeName = "cache.volume";
        const string secretName = "api-secret";
        ResourceManifest resource = CreateManifest(
            "orders-api",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TEST_MOUNT_CAPTURE_PATH"] = capture,
                ["TEST_MOUNT_NAMES"] = $"{volumeName};{secretName}",
                [ResourceEnvironment.Mount(volumeName)] = Path.Combine(root, "spoofed-cache"),
            },
            readiness: TcpProbe(),
            startup: NoneProbe(),
            liveness: NoneProbe(),
            mounts:
            [
                new ResourceManifestMount
                {
                    Name = volumeName,
                    Kind = ResourceMountKind.Volume,
                    ContainerPath = "/cache",
                    Size = "1Gi",
                },
                new ResourceManifestMount
                {
                    Name = secretName,
                    Kind = ResourceMountKind.Secret,
                    ContainerPath = "/run/secrets/api",
                },
            ]) with
        {
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = WorkloadKind.StatefulSet,
                RestartPolicy = "Never",
            },
        };
        LocalGateway gateway = CreateGateway(root);
        IApplication application = BuildApplication(gateway, resource);
        using var cancellation = new CancellationTokenSource();

        // Act
        Task run = application.RunAsync(cancellation.Token);

        try
        {
            await WaitForStateAsync(gateway, ResourceIdOf(resource.Name), ResourceLifecycle.Running);
            await WaitForFileAsync(capture);

            // Assert
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(capture));
            AssertCapturedMount(document, root, resource.Name, volumeName, "directory");
            AssertCapturedMount(document, root, resource.Name, secretName, "file");
        }
        finally
        {
            await StopApplicationAsync(cancellation, run);
            DeleteTestDirectory(root);
        }
    }

    private static string TestHostPath => Path.Combine(
        AppContext.BaseDirectory,
        TestHostAssembly + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));

    private static LocalGateway CreateGateway(string root, Action<LocalGatewayOptions>? configure = null)
    {
        var options = new LocalGatewayOptions
        {
            BaseDirectory = AppContext.BaseDirectory,
            StateDirectory = Path.Combine(root, ".cohesion"),
            ProbeInterval = TimeSpan.FromMilliseconds(50),
            ProbeTimeout = TimeSpan.FromSeconds(2),
            ReadinessBudget = TimeSpan.FromSeconds(10),
            InitialRestartBackoff = TimeSpan.FromMilliseconds(100),
            MaximumRestartBackoff = TimeSpan.FromSeconds(1),
            StopGrace = TimeSpan.FromSeconds(5),
        };
        configure?.Invoke(options);
        return new LocalGateway(options);
    }

    private static IApplication BuildApplication(
        LocalGateway gateway,
        ResourceManifest manifest,
        string[]? args = null)
    {
        IApplicationBuilder builder = Assimalign.Cohesion.ApplicationModel.Application
            .CreateBuilder(ApplicationName.Parse(ApplicationNameValue), args ?? [])
            .UseGateway(gateway);
        builder.AddResource(manifest);
        return builder.Build();
    }

    private static IApplication BuildApplication(
        LocalGateway gateway,
        IReadOnlyList<ResourceManifest> manifests)
    {
        IApplicationBuilder builder = Assimalign.Cohesion.ApplicationModel.Application
            .CreateBuilder(ApplicationName.Parse(ApplicationNameValue), [])
            .UseGateway(gateway);
        foreach (ResourceManifest manifest in manifests)
        {
            builder.AddResource(manifest);
        }

        return builder.Build();
    }

    private static ResourceManifest CreateManifest(
        string name,
        IReadOnlyDictionary<string, string>? environment,
        ResourceManifestProbe? readiness,
        ResourceManifestProbe? startup,
        ResourceManifestProbe? liveness,
        string restartPolicy = "Never",
        IReadOnlyList<ResourceManifestMount>? mounts = null,
        int stopGraceSeconds = 30)
    {
        return new ResourceManifest
        {
            Name = (ResourceName)name,
            Application = ApplicationName.Parse(ApplicationNameValue),
            Kind = "test",
            ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = TestHostAssembly + ".dll",
                AppHost = TestHostPath,
            },
            Endpoints = new[]
            {
                new ResourceManifestEndpoint
                {
                    Name = "http",
                    Scheme = "http",
                    Protocol = "tcp",
                    ContainerPort = 8080,
                    Public = true,
                },
            },
            Probes = new ResourceManifestProbes
            {
                Readiness = readiness,
                Startup = startup,
                Liveness = liveness,
            },
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = "http",
                Path = "/cohesion/v1",
            },
            Mounts = mounts ?? Array.Empty<ResourceManifestMount>(),
            EnvironmentVariables = environment ?? new Dictionary<string, string>(),
            Lifecycle = new ResourceManifestLifecycle
            {
                RestartPolicy = restartPolicy,
                StopGraceSeconds = stopGraceSeconds,
            },
        };
    }

    private static ResourceManifestProbe HttpProbe(string path) => new()
    {
        Endpoint = "http",
        Http = path,
    };

    private static ResourceManifestProbe TcpProbe() => new()
    {
        Endpoint = "http",
        Tcp = true,
    };

    private static ResourceManifestProbe NoneProbe() => new() { None = true };

    private static ResourceManifestEndpoint CreateEndpoint(string name, int containerPort) => new()
    {
        Name = name,
        Scheme = "http",
        Protocol = "tcp",
        ContainerPort = containerPort,
    };

    private static ResourceManifestControlPlane CreateControlPlane(string endpoint) => new()
    {
        Endpoint = endpoint,
        Path = "/cohesion/v1",
    };

    private static ResourceManifestReference CreateReference(
        string resource,
        string endpoint,
        bool optional = false) => new()
        {
            Resource = resource,
            Application = ApplicationNameValue,
            Endpoints = [endpoint],
            Optional = optional,
            Manifest = $"Assimalign.Cohesion.Test.{resource}.Manifest",
        };

    private static string[] DependencyVariables(string resource, string endpoint) =>
    [
        ResourceEnvironment.Dependency(resource, endpoint, "URL"),
        ResourceEnvironment.Dependency(resource, endpoint, "HOST"),
        ResourceEnvironment.Dependency(resource, endpoint, "PORT"),
        ResourceEnvironment.Dependency(resource, endpoint, "SCHEME"),
    ];

    private static void AssertCapturedMount(
        JsonDocument document,
        string root,
        ResourceName resource,
        string mount,
        string expectedKind)
    {
        string variable = ResourceEnvironment.Mount(mount);
        JsonElement captured = document.RootElement.GetProperty(variable);
        captured.GetProperty("exists").GetBoolean().ShouldBeTrue();
        captured.GetProperty("kind").GetString().ShouldBe(expectedKind);
        captured.GetProperty("path").GetString().ShouldBe(
            Path.Combine(root, ".cohesion", ApplicationNameValue, resource.ToString(), mount));
    }

    private static async Task RunAndAssertRunningAsync(string root, ResourceManifest manifest)
    {
        LocalGateway gateway = CreateGateway(root);
        IApplication application = BuildApplication(gateway, manifest);
        using var cancellation = new CancellationTokenSource();
        Task run = application.RunAsync(cancellation.Token);

        try
        {
            await WaitForStateAsync(gateway, ResourceIdOf(manifest.Name), ResourceLifecycle.Running);
        }
        finally
        {
            await StopApplicationAsync(cancellation, run);
            DeleteTestDirectory(root);
        }
    }

    private static async Task<int> RunOnceAndGetPortAsync(string root, string capture)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TEST_ENV_CAPTURE_PATH"] = capture,
        };
        ResourceManifest manifest = CreateManifest(
            "svc",
            environment,
            readiness: TcpProbe(),
            startup: NoneProbe(),
            liveness: NoneProbe());
        LocalGateway gateway = CreateGateway(root);
        IApplication application = BuildApplication(gateway, manifest);
        using var cancellation = new CancellationTokenSource();
        Task run = application.RunAsync(cancellation.Token);

        try
        {
            await WaitForStateAsync(gateway, ResourceIdOf(manifest.Name), ResourceLifecycle.Running);
            IReadOnlyList<ResourceEndpoint> endpoints = gateway.ResourceStates.GetObservedEndpoints(
                ResourceIdOf(manifest.Name));
            endpoints.Count.ShouldBe(1);
            endpoints[0].Host.ShouldBe("127.0.0.1");
            return endpoints[0].Port;
        }
        finally
        {
            await StopApplicationAsync(cancellation, run);
        }
    }

    private static async Task WaitForStateAsync(
        LocalGateway gateway,
        ResourceId resource,
        ResourceLifecycle expected)
    {
        var terminals = new HashSet<ResourceLifecycle>
        {
            expected,
            ResourceLifecycle.Failed,
            ResourceLifecycle.Stopped,
        };
        ResourceLifecycle reached = await gateway.ResourceStates
            .WaitForStateAsync(resource, terminals, TestTimeout)
            .WaitAsync(TestTimeout);
        reached.ShouldBe(expected);
    }

    private static async Task WaitForFileAsync(string path)
    {
        await WaitForConditionAsync(() => File.Exists(path), $"File '{path}' was not created.");
    }

    private static async Task WaitForTextAsync(string path, string expected)
    {
        await WaitForConditionAsync(
            () => File.Exists(path)
                  && string.Equals(File.ReadAllText(path).Trim(), expected, StringComparison.Ordinal),
            $"File '{path}' did not contain '{expected}'.");
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, string failure)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TestTimeout)
        {
            try
            {
                if (condition())
                {
                    return;
                }
            }
            catch (IOException)
            {
                // An atomic producer may still have the file open.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        throw new TimeoutException(failure);
    }

    private static async Task StopApplicationAsync(CancellationTokenSource cancellation, Task run)
    {
        cancellation.Cancel();
        await run.WaitAsync(TestTimeout);
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            values.Add(property.Name, property.Value.GetString()!);
        }

        return values;
    }

    private static async Task<OrphanProcess> StartOrphanAsync(
        string root,
        ResourceManifest manifest)
    {
        string stateDirectory = Path.Combine(root, ".cohesion");
        ApplicationName application = ApplicationName.Parse(ApplicationNameValue);
        var environment = new Dictionary<string, string>(
            manifest.EnvironmentVariables,
            StringComparer.Ordinal)
        {
            [ResourceEnvironment.Application] = ApplicationNameValue,
            [ResourceEnvironment.Resource] = manifest.Name.ToString(),
            [ResourceEnvironment.Gateway] = "local",
            [ResourceEnvironment.ContentRoot] = Path.GetDirectoryName(TestHostPath)!,
        };
        var ports = new LocalPortStore(stateDirectory);
        var endpoints = new ResourceEndpoint[manifest.Endpoints.Count];
        for (int index = 0; index < endpoints.Length; index++)
        {
            ResourceManifestEndpoint endpoint = manifest.Endpoints[index];
            endpoints[index] = new ResourceEndpoint(
                endpoint.Name,
                endpoint.Scheme,
                endpoint.ContainerPort,
                endpoint.Public);
        }

        await ports.ResolveAsync(
            application,
            manifest.Name,
            endpoints,
            environment,
            CancellationToken.None);

        EventWaitHandle? stopEvent = null;
        string? stopEventName = null;
        if (OperatingSystem.IsWindows())
        {
            stopEventName = $"Global\\cohesion-orphan-{Guid.NewGuid():N}-stop";
            stopEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.ManualReset,
                stopEventName,
                out _);
            environment[ResourceEnvironment.StopEvent] = stopEventName;
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = TestHostPath,
                WorkingDirectory = Path.GetDirectoryName(TestHostPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        if (OperatingSystem.IsWindows())
        {
            process.StartInfo.CreateNewProcessGroup = true;
        }

        foreach ((string name, string value) in environment)
        {
            process.StartInfo.Environment[name] = value;
        }

        bool started = false;
        try
        {
            process.Start().ShouldBeTrue();
            started = true;
            var registration = new LocalProcessRegistration
            {
                RegistrationId = Guid.NewGuid(),
                ProcessId = process.Id,
                StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                ExecutablePath = CanonicalizePath(TestHostPath),
                HasProcessGroup = OperatingSystem.IsWindows(),
                StopEventName = stopEventName,
            };
            await new LocalProcessStateStore(stateDirectory).SaveAsync(
                application,
                manifest.Name,
                registration,
                CancellationToken.None);
            return new OrphanProcess(process, stopEvent);
        }
        catch
        {
            stopEvent?.Dispose();
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TestTimeout);
            }

            process.Dispose();
            throw;
        }
    }

    private static async Task DisposeOrphanAsync(OrphanProcess orphan)
    {
        try
        {
            if (!orphan.Process.HasExited)
            {
                orphan.Process.Kill(entireProcessTree: true);
                await orphan.Process.WaitForExitAsync().WaitAsync(TestTimeout);
            }
        }
        finally
        {
            orphan.Process.Dispose();
            orphan.StopEvent?.Dispose();
        }
    }

    private static string CanonicalizePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        FileSystemInfo? target = File.ResolveLinkTarget(fullPath, returnFinalTarget: true);
        return target?.FullName ?? fullPath;
    }

    private static string ProcessPath(string root, ResourceName resource)
        => Path.Combine(root, ".cohesion", ApplicationNameValue, ".state", resource.ToString(), "pid");

    private static int ReadProcessId(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("processId").GetInt32();
    }

    private static bool HasPortAllocation(string root, ResourceName resource)
    {
        string path = Path.Combine(
            root,
            ".cohesion",
            ApplicationNameValue,
            ".state",
            "ports.json");
        if (!File.Exists(path))
        {
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement
            .GetProperty("resources")
            .TryGetProperty(resource.ToString(), out _);
    }

    private static bool ProcessExists(int processId)
    {
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

    private static int IndexOf(
        IReadOnlyList<StateObservation> observations,
        ResourceLifecycle state,
        int start = 0)
    {
        for (int index = start; index < observations.Count; index++)
        {
            if (observations[index].State == state)
            {
                return index;
            }
        }

        return -1;
    }

    private static int CountLinesContaining(string path, string value)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        int count = 0;
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.Contains(value, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static ResourceId ResourceIdOf(ResourceName name)
        => Guid.AsDeterministicGuid(name);

    private static string CreateTestDirectory()
        => Directory.CreateTempSubdirectory("cohesion-gateway-").FullName;

    private static void DeleteTestDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private readonly record struct StateObservation(
        ResourceLifecycle State,
        string? Detail,
        long Timestamp);

    private sealed record OrphanProcess(Process Process, EventWaitHandle? StopEvent);
}
