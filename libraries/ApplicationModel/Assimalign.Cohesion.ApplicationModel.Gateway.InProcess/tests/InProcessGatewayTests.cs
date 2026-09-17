using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Tests.TestObjects;

using HostingResourceMount = Assimalign.Cohesion.Hosting.Resources.ResourceMount;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Tests;

public sealed class InProcessGatewayTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel.Gateway.InProcess] - ";

    [Fact(DisplayName = DisplayPrefix + "telemetry reaches invocation values and removes the protected carrier when absent")]
    public async Task CreateContext_Telemetry_ShouldMaterializeAmbientValues()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            TestControlContext control = TestControlContext.Create(root, resourceName: "app-web");
            var artifact = new InProcessResourceArtifact(control.Resource.Id, Assembly.GetExecutingAssembly(), root);
            var injection = new ResourceTelemetryInjection(new Uri("https://localhost:4318"), "Authorization: Bearer test\n"u8.ToArray());
            var compilation = InProcessPlanController.Compile(control.Plan, artifact, control.Inputs, control.ObservedDependencies, injection);
            var contexts = new InProcessContextFactory(root);
            var configuration = await contexts.CreateAsync(control, compilation, new ResourceContext(contentRootPath: root), CancellationToken.None);
            configuration.ResourceContext.TryGetEnvironmentValue(ResourceEnvironment.TelemetryEndpoint, out string? endpoint).ShouldBeTrue();
            endpoint.ShouldBe("https://localhost:4318");
            configuration.ResourceContext.TryGetEnvironmentValue(ResourceEnvironment.TelemetryProtocol, out string? protocol).ShouldBeTrue();
            protocol.ShouldBe("otlp-http");
            configuration.ResourceContext.TryGetEnvironmentValue(ResourceEnvironment.TelemetryHeadersPath, out string? path).ShouldBeTrue();
            new HostingResourceMount(path!).ReadAllBytes().ShouldBe("Authorization: Bearer test\n"u8.ToArray());
            var absent = InProcessPlanController.Compile(control.Plan, artifact, control.Inputs, control.ObservedDependencies);
            var next = await contexts.CreateAsync(control, absent, new ResourceContext(contentRootPath: root), CancellationToken.None);
            next.ResourceContext.TryGetEnvironmentValue(ResourceEnvironment.TelemetryEndpoint, out _).ShouldBeFalse();
            next.ResourceContext.TryGetEnvironmentValue(ResourceEnvironment.TelemetryProtocol, out _).ShouldBeFalse();
            next.ResourceContext.TryGetEnvironmentValue(ResourceEnvironment.TelemetryHeadersPath, out _).ShouldBeFalse();
            File.Exists(path).ShouldBeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact(DisplayName = DisplayPrefix + "plain executable is refused by name before startup")]
    public void Build_WithPlainExecutable_ShouldRefuseColocation()
    {
        using var gateway = new DisposableGateway(new InProcessGateway());
        IApplicationBuilder builder = Application.CreateBuilder(
            (ApplicationName)"inprocess-tests",
            Array.Empty<string>());
        builder.AddResource(new PlainResource("plain-exe"));
        builder.UseGateway(gateway.Value);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => builder.Build());

        exception.Message.ShouldContain("plain-exe");
        exception.Message.ShouldContain("plain executables are never nested");
    }

    [Fact(DisplayName = DisplayPrefix + "render emits an in-process host unit without state or entry invocation")]
    public async Task RenderAsync_WithBoundModel_ShouldRemainOffline()
    {
        // Arrange
        string root = Path.Combine(
            Path.GetTempPath(),
            "cohesion-inprocess-render-" + Guid.NewGuid().ToString("N"));
        string stateDirectory = Path.Combine(root, "state");
        string contentRoot = Path.Combine(root, "content-that-does-not-exist");
        ResourceManifest manifest = TestManifest("rendered-member", mount: true, publicEndpoint: true);
        var resource = new ManifestResource(manifest);
        ResourcePlan plan = TestPlan("rendered-member", mount: true, exposure: true);
        var descriptor = new TestDescriptor(resource, plan);
        InProcessResourceBindings.Register(
            resource,
            new InProcessResourceBinding(Assembly.GetExecutingAssembly(), contentRoot));
        var model = new TestApplicationModel(descriptor, manifest);
        using var gateway = new DisposableGateway(new InProcessGateway(
            new InProcessGatewayOptions { StateDirectory = stateDirectory }));
        using var output = new StringWriter();

        // Act
        await ((IApplicationGatewayRenderer)gateway.Value).RenderAsync([model], output);

        // Assert
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement rootElement = document.RootElement;
        rootElement.GetProperty("schema").GetString().ShouldBe("cohesion/local-plan-set/v1");
        rootElement.GetProperty("gateway").GetString().ShouldBe("inprocess");
        JsonElement resourceElement = rootElement
            .GetProperty("applications")[0]
            .GetProperty("resources")[0];
        resourceElement.TryGetProperty("plan", out _).ShouldBeFalse();
        JsonElement unit = resourceElement.GetProperty("unit");
        unit.GetProperty("kind").GetString().ShouldBe("inProcessHost");
        unit.GetProperty("artifact").GetProperty("identity").GetString()
            .ShouldBe(manifest.Artifact.Assembly);
        unit.GetProperty("artifact").GetProperty("contentRoot").GetString()
            .ShouldBe(contentRoot);
        unit.GetProperty("endpoints")[0].GetProperty("allocation").GetString()
            .ShouldBe("ambient");
        unit.GetProperty("endpoints")[0].GetProperty("public").GetBoolean().ShouldBeTrue();
        unit.GetProperty("mounts")[0].GetProperty("materialization").GetString()
            .ShouldBe("ambientHandle");
        output.ToString().ShouldNotContain("credential", Case.Insensitive);
        Directory.Exists(root).ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "unregistered executable is refused by resource name before startup")]
    public void Build_WithUnregisteredExecutableBinding_ShouldRefuseColocation()
    {
        using var gateway = new DisposableGateway(new InProcessGateway());
        IApplicationBuilder builder = Application.CreateBuilder(
            (ApplicationName)"inprocess-tests",
            Array.Empty<string>());
        var resource = new ManifestResource(TestManifest(
            "unregistered-executable",
            mount: false));
        builder.AddResource(resource);
        InProcessResourceBindings.Register(
            resource,
            new InProcessResourceBinding(
                Assembly.GetExecutingAssembly(),
                Path.GetTempPath()));
        builder.UseGateway(gateway.Value);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => builder.Build());

        exception.Message.ShouldContain("unregistered-executable");
        exception.Message.ShouldContain("did not register an enabled resource entry point");
    }

    [Fact(DisplayName = DisplayPrefix + "compiler refuses Job and unsupported probe kinds with resource names")]
    public void CanRealize_WithUnsupportedPlans_ShouldReturnNamedReason()
    {
        var contexts = new InProcessContextFactory(Path.GetTempPath());
        using var host = new ProcessHost("Testing", Path.GetTempPath());
        var controller = new InProcessPlanController(
            contexts,
            new NoOpSupervisor(),
            new ResourceContext(contentRootPath: Path.GetTempPath()));
        ResourcePlan job = TestPlan(
            "worker",
            WorkloadKind.Job,
            [new ProbeMapping("readiness", null, ProbeKind.None, null, [])]);

        bool accepted = controller.CanRealize(job, out string? reason);

        accepted.ShouldBeFalse();
        reason.ShouldNotBeNull();
        reason.ShouldContain("worker");
        reason.ShouldContain("Job");
    }

    [Fact(DisplayName = DisplayPrefix + "compiler accepts public exposure backed by a loopback TCP endpoint")]
    public void CanRealize_WithPublicTcpExposure_ShouldAcceptPlan()
    {
        var contexts = new InProcessContextFactory(Path.GetTempPath());
        var controller = new InProcessPlanController(
            contexts,
            new NoOpSupervisor(),
            new ResourceContext(contentRootPath: Path.GetTempPath()));
        ResourceManifest manifest = TestManifest("public-web", mount: false, publicEndpoint: true);
        ResourcePlan exposed = GenericPlanner.CreatePlan(
            new PlanContext(
                manifest,
                new ResourceOptions(),
                new TestEnvironment(),
                new Dictionary<string, ResourceManifest>()));

        bool accepted = controller.CanRealize(exposed, out string? reason);

        accepted.ShouldBeTrue();
        reason.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "compiler refuses unsupported public exposure with the resource name")]
    public void CanRealize_WithUnsupportedPublicExposure_ShouldReturnNamedReason()
    {
        var contexts = new InProcessContextFactory(Path.GetTempPath());
        var controller = new InProcessPlanController(
            contexts,
            new NoOpSupervisor(),
            new ResourceContext(contentRootPath: Path.GetTempPath()));
        ResourcePlan exposed = TestPlan(
            "public-datagram",
            exposure: true,
            exposureProtocol: "udp");

        bool accepted = controller.CanRealize(exposed, out string? reason);

        accepted.ShouldBeFalse();
        reason.ShouldNotBeNull();
        reason.ShouldContain("public-datagram");
        reason.ShouldContain("public exposure");
    }

    [Fact(DisplayName = DisplayPrefix + "completed entry surrendering an idle host is parent-owned")]
    public async Task AddAsync_WithCompletedIdleEntry_ShouldOwnHostLifetime()
    {
        string stateDirectory = CreateTemporaryDirectory();
        try
        {
            await using var processHost = new AsyncDisposableHost(
                new ProcessHost("Testing", stateDirectory));
            await ((IHost)processHost.Value).StartAsync(CancellationToken.None);
            var programCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            programCompletion.TrySetResult();
            var memberHost = new TestMemberHost(programCompletion);
            var invocation = new TestInvocation(
                Task.FromResult<IHost>(memberHost),
                programCompletion.Task);
            var context = new ResourceContext(
                resourceName: "idle-member",
                environmentName: "Testing",
                gatewayName: "inprocess",
                contentRootPath: stateDirectory);

            ProcessHostLease lease = await processHost.Value.AddAsync(
                memberHost,
                invocation,
                context,
                CancellationToken.None);

            memberHost.Context.State.ShouldBe(HostState.Started);
            lease.Completion.IsCompleted.ShouldBeFalse();

            await processHost.Value.RemoveAsync(
                lease,
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            memberHost.StopCount.ShouldBe(1);
            memberHost.DisposeCount.ShouldBe(1);
            memberHost.DisposeContext.ShouldBeSameAs(context);
            lease.Completion.IsCompleted.ShouldBeTrue();
            await ((IHost)processHost.Value).StopAsync(CancellationToken.None);
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "remove releases and disposes a member after stop faults")]
    public async Task RemoveAsync_WhenMemberStopFails_ShouldStillDisposeAndReleaseLease()
    {
        string stateDirectory = CreateTemporaryDirectory();
        try
        {
            await using var processHost = new AsyncDisposableHost(
                new ProcessHost("Testing", stateDirectory));
            await ((IHost)processHost.Value).StartAsync(CancellationToken.None);
            var memberHost = new FailingStopMemberHost();
            var invocation = new TestInvocation(
                Task.FromResult<IHost>(memberHost),
                Task.CompletedTask);
            var context = new ResourceContext(
                resourceName: "failing-stop",
                environmentName: "Testing",
                gatewayName: "inprocess",
                contentRootPath: stateDirectory);
            ProcessHostLease lease = await processHost.Value.AddAsync(
                memberHost,
                invocation,
                context,
                CancellationToken.None);

            InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
                () => processHost.Value.RemoveAsync(
                    lease,
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None));

            exception.Message.ShouldBe("member stop failed");
            memberHost.DisposeCount.ShouldBe(1);
            memberHost.DisposeContext.ShouldBeSameAs(context);
            processHost.Value.Context.HostedServices.ShouldBeEmpty();
            await ((IHost)processHost.Value).StopAsync(CancellationToken.None);
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "cancelled adoption returns before a cancellation-ignoring member settles")]
    public async Task AddAsync_WhenMemberStartIgnoresCancellation_ShouldDeferCleanup()
    {
        string stateDirectory = CreateTemporaryDirectory();
        var releaseStart = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var programCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await using var processHost = new AsyncDisposableHost(
                new ProcessHost("Testing", stateDirectory));
            await ((IHost)processHost.Value).StartAsync(CancellationToken.None);
            var memberHost = new DelayedStartMemberHost(releaseStart.Task);
            var invocation = new TestInvocation(
                Task.FromResult<IHost>(memberHost),
                programCompletion.Task);
            var context = new ResourceContext(
                resourceName: "delayed-start",
                environmentName: "Testing",
                gatewayName: "inprocess",
                contentRootPath: stateDirectory);
            using var cancellationTokenSource = new CancellationTokenSource();
            Task<ProcessHostLease> add = processHost.Value.AddAsync(
                memberHost,
                invocation,
                context,
                cancellationTokenSource.Token);
            await memberHost.StartEntered.WaitAsync(TimeSpan.FromSeconds(5));

            cancellationTokenSource.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(
                () => add.WaitAsync(TimeSpan.FromSeconds(1)));

            releaseStart.TrySetResult();
            await WaitUntilAsync(
                () => memberHost.Context.State is HostState.Failed,
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            programCompletion.TrySetException(new OperationCanceledException());
            await WaitUntilAsync(
                () => memberHost.DisposeCount == 1,
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

            memberHost.DisposeContext.ShouldBeSameAs(context);
            processHost.Value.Context.HostedServices.ShouldBeEmpty();
            await ((IHost)processHost.Value).StopAsync(CancellationToken.None);
        }
        finally
        {
            releaseStart.TrySetResult();
            programCompletion.TrySetCanceled();
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "console output is line-prefixed by ambient resource")]
    public void ConsoleRouter_WithInterleavedContexts_ShouldWriteAtomicPrefixedLines()
    {
        var output = new StringWriter();
        var writer = new AmbientPrefixTextWriter(output);
        ResourceContext web = new(resourceName: "web", contentRootPath: Path.GetTempPath());
        ResourceContext database = new(resourceName: "database", contentRootPath: Path.GetTempPath());

        using (ResourceRuntime.CreateScope(web))
        {
            writer.Write("web ");
        }
        using (ResourceRuntime.CreateScope(database))
        {
            writer.WriteLine("database");
        }
        using (ResourceRuntime.CreateScope(web))
        {
            writer.WriteLine("continued");
        }

        writer.FlushPending();
        output.ToString().ShouldBe(
            $"[database] database{Environment.NewLine}[web] web continued{Environment.NewLine}");
    }

    [Fact(DisplayName = DisplayPrefix + "composite endpoints references and mount paths remap inward")]
    public async Task CreateContext_WithOuterCompositeValues_ShouldRemapMemberInputs()
    {
        string stateDirectory = CreateTemporaryDirectory();
        string outerMountPath = Path.Combine(stateDirectory, "outer-claim");
        Directory.CreateDirectory(outerMountPath);
        try
        {
            Uri outerHttp = new("http://127.0.0.1:43121/");
            Uri outerDatabase = new("http://127.0.0.1:43122/");
            var outerMount = HostingResourceMount.FromBytes("outer"u8, outerMountPath);
            var outer = new ResourceContext(
                applicationName: "outer-app",
                resourceName: "composite",
                environmentName: "Testing",
                gatewayName: "inprocess",
                contentRootPath: stateDirectory,
                endpoints: new Dictionary<string, Uri>
                {
                    ["WEB_HTTP"] = outerHttp,
                },
                mounts: new Dictionary<string, HostingResourceMount>
                {
                    ["COMPOSITE_WEB_CONFIG"] = outerMount,
                },
                references: new Dictionary<string, Uri>
                {
                    ["composite:database-admin"] = outerDatabase,
                });
            TestControlContext control = TestControlContext.Create(
                stateDirectory,
                resourceName: "app-web",
                mount: true,
                observations:
                [
                    new ResourceDependencyObservation(
                        (ApplicationName)"app",
                        (ResourceName)"database",
                        ResourceLifecycle.Running,
                        ["admin"],
                        [],
                        optional: false),
                ]);
            var factory = new InProcessContextFactory(stateDirectory);
            var artifact = new InProcessResourceArtifact(
                control.Resource.Id,
                Assembly.GetExecutingAssembly(),
                stateDirectory);
            InProcessPlanCompilation compilation = InProcessPlanController.Compile(
                control.Plan,
                artifact,
                control.Inputs,
                control.ObservedDependencies);

            InProcessMemberConfiguration configuration = await factory.CreateAsync(
                control,
                compilation,
                outer,
                CancellationToken.None);

            configuration.ResourceContext.Endpoints["http"].ShouldBe(outerHttp);
            configuration.ResourceContext.GetReference("database", "admin").ShouldBe(outerDatabase);
            configuration.ResourceContext.Mounts["config"].ShouldBeSameAs(outerMount);
            configuration.ResourceContext.ContentRootPath.ShouldBe(stateDirectory);
            configuration.ResourceContext.BootstrapCredential.ToArray().ShouldBe("credential"u8.ToArray());
            configuration.ResourceContext.ApplicationTrustKey.ToArray().ShouldBe("trust-key"u8.ToArray());
            configuration.StartupProbe.IsDefaultControlPlane.ShouldBeTrue();
            configuration.StartupProbe.Mapping.Role.ShouldBe("startup");
            configuration.ReadinessProbe.IsDefaultControlPlane.ShouldBeFalse();
            configuration.LivenessProbe.IsDefaultControlPlane.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "composite endpoint re-map refuses a non-loopback address")]
    public async Task CreateContext_WithRemoteOuterCompositeEndpoint_ShouldRefuseAddress()
    {
        string stateDirectory = CreateTemporaryDirectory();
        try
        {
            var outer = new ResourceContext(
                resourceName: "composite",
                environmentName: "Testing",
                contentRootPath: stateDirectory,
                endpoints: new Dictionary<string, Uri>
                {
                    ["WEB_HTTP"] = new Uri("http://192.0.2.1:43121/"),
                });
            TestControlContext control = TestControlContext.Create(
                stateDirectory,
                resourceName: "app-web");
            var factory = new InProcessContextFactory(stateDirectory);
            var artifact = new InProcessResourceArtifact(
                control.Resource.Id,
                Assembly.GetExecutingAssembly(),
                stateDirectory);
            InProcessPlanCompilation compilation = InProcessPlanController.Compile(
                control.Plan,
                artifact,
                control.Inputs,
                control.ObservedDependencies);

            InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
                () => factory.CreateAsync(
                    control,
                    compilation,
                    outer,
                    CancellationToken.None));

            exception.Message.ShouldContain("web");
            exception.Message.ShouldContain("http");
            exception.Message.ShouldContain("loopback address");
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "context honors plan endpoint overrides without mutating process environment")]
    public async Task CreateContext_WithEndpointOverride_ShouldUseAmbientBinding()
    {
        string stateDirectory = CreateTemporaryDirectory();
        try
        {
            const int overriddenPort = 43124;
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ResourceEnvironment.Endpoint("http", "HOST")] = "127.0.0.1",
                [ResourceEnvironment.Endpoint("http", "PORT")] = "43124",
                [ResourceEnvironment.Endpoint("http", "SCHEME")] = "http",
            };
            TestControlContext control = TestControlContext.Create(
                stateDirectory,
                resourceName: "web",
                environment: environment);
            var factory = new InProcessContextFactory(stateDirectory);
            var artifact = new InProcessResourceArtifact(
                control.Resource.Id,
                Assembly.GetExecutingAssembly(),
                stateDirectory);
            InProcessPlanCompilation compilation = InProcessPlanController.Compile(
                control.Plan,
                artifact,
                control.Inputs,
                control.ObservedDependencies);

            InProcessMemberConfiguration configuration = await factory.CreateAsync(
                control,
                compilation,
                new ResourceContext(contentRootPath: stateDirectory),
                CancellationToken.None);

            configuration.ResourceContext.Endpoints["http"].ShouldBe(
                new Uri($"http://127.0.0.1:{overriddenPort}/"));
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "remote plan endpoint overrides are refused")]
    public async Task CreateContext_WithRemoteEndpointOverride_ShouldRefuseAddress()
    {
        string stateDirectory = CreateTemporaryDirectory();
        try
        {
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ResourceEnvironment.Endpoint("http", "HOST")] = "192.0.2.2",
                [ResourceEnvironment.Endpoint("http", "PORT")] = "43125",
            };
            TestControlContext control = TestControlContext.Create(
                stateDirectory,
                resourceName: "web",
                environment: environment);
            var factory = new InProcessContextFactory(stateDirectory);
            var artifact = new InProcessResourceArtifact(
                control.Resource.Id,
                Assembly.GetExecutingAssembly(),
                stateDirectory);
            InProcessPlanCompilation compilation = InProcessPlanController.Compile(
                control.Plan,
                artifact,
                control.Inputs,
                control.ObservedDependencies);

            InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
                () => factory.CreateAsync(
                    control,
                    compilation,
                    new ResourceContext(contentRootPath: stateDirectory),
                    CancellationToken.None));

            exception.Message.ShouldContain("web");
            exception.Message.ShouldContain("http");
            exception.Message.ShouldContain("loopback address");
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "remote composite member references are refused")]
    public async Task CreateContext_WithRemoteOuterCompositeReference_ShouldRefuseAddress()
    {
        string stateDirectory = CreateTemporaryDirectory();
        try
        {
            TestControlContext control = TestControlContext.Create(
                stateDirectory,
                resourceName: "app-web",
                observations:
                [
                    new ResourceDependencyObservation(
                        (ApplicationName)"app",
                        (ResourceName)"database",
                        ResourceLifecycle.Running,
                        ["admin"],
                        [],
                        optional: false),
                ]);
            var factory = new InProcessContextFactory(stateDirectory);
            var artifact = new InProcessResourceArtifact(
                control.Resource.Id,
                Assembly.GetExecutingAssembly(),
                stateDirectory);
            InProcessPlanCompilation compilation = InProcessPlanController.Compile(
                control.Plan,
                artifact,
                control.Inputs,
                control.ObservedDependencies);
            var outer = new ResourceContext(
                resourceName: "composite",
                contentRootPath: stateDirectory,
                references: new Dictionary<string, Uri>
                {
                    ["composite:database-admin"] = new Uri("http://192.0.2.3:43126/"),
                });

            InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
                () => factory.CreateAsync(
                    control,
                    compilation,
                    outer,
                    CancellationToken.None));

            exception.Message.ShouldContain("database:admin");
            exception.Message.ShouldContain("loopback address");
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "missing optional dependencies are not injected")]
    public async Task CreateContext_WithMissingOptionalDependency_ShouldOmitReference()
    {
        string stateDirectory = CreateTemporaryDirectory();
        try
        {
            TestControlContext control = TestControlContext.Create(
                stateDirectory,
                resourceName: "web",
                observations:
                [
                    new ResourceDependencyObservation(
                        (ApplicationName)"app",
                        (ResourceName)"database",
                        ResourceLifecycle.Unknown,
                        ["db"],
                        [new ResourceEndpoint("db", "tcp", 43125, Host: "127.0.0.1")],
                        optional: true),
                ]);
            var factory = new InProcessContextFactory(stateDirectory);
            var artifact = new InProcessResourceArtifact(
                control.Resource.Id,
                Assembly.GetExecutingAssembly(),
                stateDirectory);
            InProcessPlanCompilation compilation = InProcessPlanController.Compile(
                control.Plan,
                artifact,
                control.Inputs,
                control.ObservedDependencies);

            InProcessMemberConfiguration configuration = await factory.CreateAsync(
                control,
                compilation,
                new ResourceContext(contentRootPath: stateDirectory),
                CancellationToken.None);

            configuration.ResourceContext.TryGetReference("database", "db", out _)
                .ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Theory(DisplayName = DisplayPrefix + "degraded dependency references remain injected")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateContext_WithDegradedDependency_ShouldRetainReference(bool optional)
    {
        string stateDirectory = CreateTemporaryDirectory();
        try
        {
            Uri database = new("tcp://127.0.0.1:43125/");
            TestControlContext control = TestControlContext.Create(
                stateDirectory,
                resourceName: "web",
                observations:
                [
                    new ResourceDependencyObservation(
                        (ApplicationName)"app",
                        (ResourceName)"database",
                        ResourceLifecycle.Degraded,
                        ["db"],
                        [new ResourceEndpoint("db", "tcp", 43125, Host: "127.0.0.1")],
                        optional),
                ]);
            var factory = new InProcessContextFactory(stateDirectory);
            var artifact = new InProcessResourceArtifact(
                control.Resource.Id,
                Assembly.GetExecutingAssembly(),
                stateDirectory);
            InProcessPlanCompilation compilation = InProcessPlanController.Compile(
                control.Plan,
                artifact,
                control.Inputs,
                control.ObservedDependencies);

            InProcessMemberConfiguration configuration = await factory.CreateAsync(
                control,
                compilation,
                new ResourceContext(contentRootPath: stateDirectory),
                CancellationToken.None);

            configuration.ResourceContext.GetReference("database", "db").ShouldBe(database);
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "outer self references do not bypass dependency observations")]
    public async Task CreateContext_WithUnobservedOuterSelfReference_ShouldOmitReference()
    {
        string stateDirectory = CreateTemporaryDirectory();
        try
        {
            TestControlContext control = TestControlContext.Create(
                stateDirectory,
                resourceName: "app-web");
            var factory = new InProcessContextFactory(stateDirectory);
            var artifact = new InProcessResourceArtifact(
                control.Resource.Id,
                Assembly.GetExecutingAssembly(),
                stateDirectory);
            InProcessPlanCompilation compilation = InProcessPlanController.Compile(
                control.Plan,
                artifact,
                control.Inputs,
                control.ObservedDependencies);
            var outer = new ResourceContext(
                resourceName: "composite",
                contentRootPath: stateDirectory,
                references: new Dictionary<string, Uri>
                {
                    ["composite:web-http"] = new Uri("http://127.0.0.1:43126/"),
                });

            InProcessMemberConfiguration configuration = await factory.CreateAsync(
                control,
                compilation,
                outer,
                CancellationToken.None);

            configuration.ResourceContext.TryGetReference("web", "http", out _)
                .ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "delete removes only the validated resource state tree")]
    public async Task DeleteAsync_WithResourceClaimTree_ShouldRemoveOnlyResourceState()
    {
        string stateDirectory = CreateTemporaryDirectory();
        string resourceDirectory = Path.Combine(stateDirectory, "app", "web");
        string siblingFile = Path.Combine(stateDirectory, "app", "database", "mounts", "data", "value");
        try
        {
            Directory.CreateDirectory(Path.Combine(resourceDirectory, "mounts", "data"));
            Directory.CreateDirectory(Path.GetDirectoryName(siblingFile)!);
            File.WriteAllText(Path.Combine(resourceDirectory, "mounts", "data", "value"), "web");
            File.WriteAllText(siblingFile, "database");
            var factory = new InProcessContextFactory(stateDirectory);

            await factory.DeleteAsync(
                (ApplicationName)"app",
                (ResourceName)"web",
                CancellationToken.None);

            Directory.Exists(resourceDirectory).ShouldBeFalse();
            File.Exists(siblingFile).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "third liveness failure stops disposes and reinvokes with the same context")]
    public async Task Monitor_AfterThreeLivenessFailures_ShouldRestartSameContext()
    {
        string stateDirectory = CreateTemporaryDirectory();
        var options = new InProcessGatewayOptions
        {
            ProbeInterval = TimeSpan.FromMilliseconds(10),
            ProbeTimeout = TimeSpan.FromSeconds(1),
            InitialRestartBackoff = TimeSpan.Zero,
            MaximumRestartBackoff = TimeSpan.Zero,
            LivenessFailureThreshold = 3,
        };
        await using var host = new AsyncDisposableHost(
            new ProcessHost("Testing", stateDirectory));
        await ((IHost)host.Value).StartAsync(CancellationToken.None);
        var entries = new RecordingEntryInvoker();
        var probes = new RestartProbeRunner();
        var supervisor = new InProcessMemberSupervisor(
            host.Value,
            options,
            probes,
            entries);
        TestControlContext control = TestControlContext.Create(
            stateDirectory,
            resourceName: "web");
        ResourceContext ambient = new(
            applicationName: "app",
            resourceName: "web",
            environmentName: "Testing",
            gatewayName: "inprocess",
            contentRootPath: stateDirectory,
            endpoints: new Dictionary<string, Uri>
            {
                ["http"] = new Uri("http://127.0.0.1:43123/"),
            });
        var configuration = new InProcessMemberConfiguration(
            control,
            new InProcessResourceArtifact(
                control.Resource.Id,
                Assembly.GetExecutingAssembly(),
                stateDirectory),
            ambient,
            [new ResourceEndpoint("http", "http", 43123, Host: "127.0.0.1")],
            ExplicitProbe("startup"),
            ExplicitProbe("readiness"),
            ExplicitProbe("liveness"),
            RestartPolicy.OnFailure);

        await supervisor.StartAsync(configuration, CancellationToken.None);
        await WaitUntilAsync(
            () => entries.Contexts.Count >= 2,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        entries.Contexts.Count.ShouldBe(2);
        entries.Contexts[1].ShouldBeSameAs(entries.Contexts[0]);
        entries.Hosts[0].StopCount.ShouldBe(1);
        entries.Hosts[0].DisposeCount.ShouldBe(1);
        entries.Hosts[0].DisposeContext.ShouldBeSameAs(ambient);
        control.States.ShouldContain(ResourceLifecycle.Degraded);
        control.State.GetState(control.Resource.Id).ShouldBe(ResourceLifecycle.Running);

        await supervisor.StopAsync(control, CancellationToken.None);
        await ((IHost)host.Value).StopAsync(CancellationToken.None);
        Directory.Delete(stateDirectory, recursive: true);
    }

    [Fact(DisplayName = DisplayPrefix + "restart readiness is bounded and exhausts restart attempts")]
    public async Task Monitor_WhenRestartNeverBecomesReady_ShouldExhaustRestartAttempts()
    {
        // Arrange
        string stateDirectory = CreateTemporaryDirectory();
        var timeProvider = new ManualTimeProvider();
        var options = new InProcessGatewayOptions
        {
            ReadinessBudget = TimeSpan.FromSeconds(10),
            ProbeInterval = TimeSpan.FromSeconds(1),
            ProbeTimeout = TimeSpan.FromSeconds(1),
            InitialRestartBackoff = TimeSpan.Zero,
            MaximumRestartBackoff = TimeSpan.Zero,
            LivenessFailureThreshold = 1,
            MaximumRestartAttempts = 1,
            TimeProvider = timeProvider,
        };
        await using var host = new AsyncDisposableHost(
            new ProcessHost("Testing", stateDirectory));
        await ((IHost)host.Value).StartAsync(CancellationToken.None);
        var entries = new RecordingEntryInvoker();
        var probes = new RestartReadinessTimeoutProbeRunner();
        var supervisor = new InProcessMemberSupervisor(
            host.Value,
            options,
            probes,
            entries);
        TestControlContext control = TestControlContext.Create(
            stateDirectory,
            resourceName: "web");
        InProcessMemberConfiguration configuration = TestMemberConfiguration(
            control,
            stateDirectory,
            TestAmbientContext(stateDirectory, "credential"u8.ToArray()));

        // Act
        await supervisor.StartAsync(configuration, CancellationToken.None);
        await WaitUntilAsync(
            () => control.State.GetState(control.Resource.Id) is ResourceLifecycle.Running,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        timeProvider.Advance(options.ProbeInterval);
        await probes.RestartReadinessEntered.WaitAsync(TimeSpan.FromSeconds(5));
        timeProvider.Advance(options.ReadinessBudget);
        await WaitUntilAsync(
            () => control.State.GetState(control.Resource.Id) is ResourceLifecycle.Failed,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        // Assert
        entries.Contexts.Count.ShouldBe(2);
        entries.Hosts.All(static memberHost => memberHost.DisposeCount == 1).ShouldBeTrue();
        control.LastDetail.ShouldNotBeNull().ShouldContain("exhausted restart limit 1");
        control.LastDetail.ShouldNotBeNull().ShouldContain("readiness budget");

        await supervisor.StopAsync(control, CancellationToken.None);
        await ((IHost)host.Value).StopAsync(CancellationToken.None);
        Directory.Delete(stateDirectory, recursive: true);
    }

    [Fact(DisplayName = DisplayPrefix + "restartable failures before initial readiness retry until success")]
    public async Task StartAsync_WithRepeatedRestartableStartupFailures_ShouldRetryUntilRunning()
    {
        string stateDirectory = CreateTemporaryDirectory();
        await using var host = new AsyncDisposableHost(
            new ProcessHost("Testing", stateDirectory));
        await ((IHost)host.Value).StartAsync(CancellationToken.None);
        var entries = new InitialFailureEntryInvoker(
            new ResourceEntryExitException(69, new InvalidOperationException("dependency unavailable")),
            new AggregateException(
                "wrapped runtime failure",
                new ResourceEntryExitException(75, new InvalidOperationException("runtime unavailable"))));
        var supervisor = new InProcessMemberSupervisor(
            host.Value,
            FastRestartOptions(),
            new HealthyProbeRunner(),
            entries);
        TestControlContext control = TestControlContext.Create(
            stateDirectory,
            resourceName: "web");
        InProcessMemberConfiguration configuration = TestMemberConfiguration(
            control,
            stateDirectory,
            TestAmbientContext(stateDirectory, "credential"u8.ToArray()));

        await supervisor.StartAsync(configuration, CancellationToken.None);
        await WaitUntilAsync(
            () => control.State.GetState(control.Resource.Id) is ResourceLifecycle.Running,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        entries.InvocationCount.ShouldBe(3);
        control.States.ShouldContain(ResourceLifecycle.Degraded);

        await supervisor.StopAsync(control, CancellationToken.None);
        await ((IHost)host.Value).StopAsync(CancellationToken.None);
        Directory.Delete(stateDirectory, recursive: true);
    }

    [Fact(DisplayName = DisplayPrefix + "generic pre-readiness failure is final even under Always")]
    public async Task StartAsync_WithGenericPreReadinessFailureAndAlways_ShouldNotRestart()
    {
        string stateDirectory = CreateTemporaryDirectory();
        await using var host = new AsyncDisposableHost(
            new ProcessHost("Testing", stateDirectory));
        await ((IHost)host.Value).StartAsync(CancellationToken.None);
        var entries = new InitialFailureEntryInvoker(
            new InvalidOperationException("entry registration missing"));
        var supervisor = new InProcessMemberSupervisor(
            host.Value,
            FastRestartOptions(),
            new HealthyProbeRunner(),
            entries);
        TestControlContext control = TestControlContext.Create(
            stateDirectory,
            resourceName: "named-web");
        InProcessMemberConfiguration configuration = TestMemberConfiguration(
            control,
            stateDirectory,
            TestAmbientContext(stateDirectory, "credential"u8.ToArray()),
            RestartPolicy.Always);

        await supervisor.StartAsync(configuration, CancellationToken.None);

        entries.InvocationCount.ShouldBe(1);
        control.State.GetState(control.Resource.Id).ShouldBe(ResourceLifecycle.Failed);
        control.LastDetail.ShouldNotBeNull().ShouldContain("named-web");
        control.LastDetail.ShouldNotBeNull().ShouldContain("exit 70");

        await ((IHost)host.Value).StopAsync(CancellationToken.None);
        Directory.Delete(stateDirectory, recursive: true);
    }

    [Theory(DisplayName = DisplayPrefix + "configuration and startup exits stay final under Always")]
    [InlineData(64)]
    [InlineData(70)]
    public async Task Monitor_WithFinalExitAndAlways_ShouldNotRestart(int exitCode)
    {
        string stateDirectory = CreateTemporaryDirectory();
        await using var host = new AsyncDisposableHost(
            new ProcessHost("Testing", stateDirectory));
        await ((IHost)host.Value).StartAsync(CancellationToken.None);
        var entries = new RecordingEntryInvoker();
        var supervisor = new InProcessMemberSupervisor(
            host.Value,
            FastRestartOptions(),
            new HealthyProbeRunner(),
            entries);
        TestControlContext control = TestControlContext.Create(
            stateDirectory,
            resourceName: "web");
        InProcessMemberConfiguration configuration = TestMemberConfiguration(
            control,
            stateDirectory,
            TestAmbientContext(stateDirectory, "credential"u8.ToArray()),
            RestartPolicy.Always);

        await supervisor.StartAsync(configuration, CancellationToken.None);
        await WaitUntilAsync(
            () => control.State.GetState(control.Resource.Id) is ResourceLifecycle.Running,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        entries.Completions[0].TrySetException(new ResourceEntryExitException(exitCode));
        await WaitUntilAsync(
            () => control.State.GetState(control.Resource.Id) is ResourceLifecycle.Failed,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        entries.Contexts.Count.ShouldBe(1);
        control.LastDetail.ShouldNotBeNull().ShouldContain($"exit {exitCode}");

        await supervisor.StopAsync(control, CancellationToken.None);
        await ((IHost)host.Value).StopAsync(CancellationToken.None);
        Directory.Delete(stateDirectory, recursive: true);
    }

    [Theory(DisplayName = DisplayPrefix + "dependency and runtime exits restart under OnFailure")]
    [InlineData(69)]
    [InlineData(75)]
    public async Task Monitor_WithRestartableExitAndOnFailure_ShouldRestart(int exitCode)
    {
        string stateDirectory = CreateTemporaryDirectory();
        await using var host = new AsyncDisposableHost(
            new ProcessHost("Testing", stateDirectory));
        await ((IHost)host.Value).StartAsync(CancellationToken.None);
        var entries = new RecordingEntryInvoker();
        var supervisor = new InProcessMemberSupervisor(
            host.Value,
            FastRestartOptions(),
            new HealthyProbeRunner(),
            entries);
        TestControlContext control = TestControlContext.Create(
            stateDirectory,
            resourceName: "web");
        InProcessMemberConfiguration configuration = TestMemberConfiguration(
            control,
            stateDirectory,
            TestAmbientContext(stateDirectory, "credential"u8.ToArray()));

        await supervisor.StartAsync(configuration, CancellationToken.None);
        await WaitUntilAsync(
            () => control.State.GetState(control.Resource.Id) is ResourceLifecycle.Running,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        entries.Completions[0].TrySetException(new ResourceEntryExitException(exitCode));
        await WaitUntilAsync(
            () => entries.Contexts.Count >= 2
                && control.State.GetState(control.Resource.Id) is ResourceLifecycle.Running,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        entries.Contexts.Count.ShouldBe(2);

        await supervisor.StopAsync(control, CancellationToken.None);
        await ((IHost)host.Value).StopAsync(CancellationToken.None);
        Directory.Delete(stateDirectory, recursive: true);
    }

    [Theory(DisplayName = DisplayPrefix + "clean exit restarts only under Always")]
    [InlineData(RestartPolicy.Always, true)]
    [InlineData(RestartPolicy.OnFailure, false)]
    [InlineData(RestartPolicy.Never, false)]
    public async Task Monitor_WithCleanExit_ShouldFollowAlwaysPolicy(
        RestartPolicy policy,
        bool shouldRestart)
    {
        string stateDirectory = CreateTemporaryDirectory();
        await using var host = new AsyncDisposableHost(
            new ProcessHost("Testing", stateDirectory));
        await ((IHost)host.Value).StartAsync(CancellationToken.None);
        var entries = new RecordingEntryInvoker();
        var supervisor = new InProcessMemberSupervisor(
            host.Value,
            FastRestartOptions(),
            new HealthyProbeRunner(),
            entries);
        TestControlContext control = TestControlContext.Create(
            stateDirectory,
            resourceName: "web");
        InProcessMemberConfiguration configuration = TestMemberConfiguration(
            control,
            stateDirectory,
            TestAmbientContext(stateDirectory, "credential"u8.ToArray()),
            policy);

        await supervisor.StartAsync(configuration, CancellationToken.None);
        await WaitUntilAsync(
            () => control.State.GetState(control.Resource.Id) is ResourceLifecycle.Running,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        entries.Completions[0].TrySetResult();
        await WaitUntilAsync(
            () => shouldRestart
                ? entries.Contexts.Count >= 2
                    && control.State.GetState(control.Resource.Id) is ResourceLifecycle.Running
                : control.State.GetState(control.Resource.Id) is ResourceLifecycle.Stopped,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        entries.Contexts.Count.ShouldBe(shouldRestart ? 2 : 1);

        await supervisor.StopAsync(control, CancellationToken.None);
        await ((IHost)host.Value).StopAsync(CancellationToken.None);
        Directory.Delete(stateDirectory, recursive: true);
    }

    [Fact(DisplayName = DisplayPrefix + "reconcile replaces the generation to deliver a new ambient context")]
    public async Task StartAsync_WithReconciledContext_ShouldReplaceGeneration()
    {
        string stateDirectory = CreateTemporaryDirectory();
        var options = new InProcessGatewayOptions
        {
            ProbeInterval = TimeSpan.FromMilliseconds(10),
            ProbeTimeout = TimeSpan.FromSeconds(1),
        };
        await using var host = new AsyncDisposableHost(
            new ProcessHost("Testing", stateDirectory));
        await ((IHost)host.Value).StartAsync(CancellationToken.None);
        var entries = new RecordingEntryInvoker();
        var supervisor = new InProcessMemberSupervisor(
            host.Value,
            options,
            new HealthyProbeRunner(),
            entries);
        TestControlContext control = TestControlContext.Create(
            stateDirectory,
            resourceName: "web");
        ResourceContext first = TestAmbientContext(stateDirectory, "first"u8.ToArray());
        ResourceContext second = TestAmbientContext(stateDirectory, "second"u8.ToArray());
        var firstConfiguration = TestMemberConfiguration(control, stateDirectory, first);
        var secondConfiguration = TestMemberConfiguration(control, stateDirectory, second);

        await supervisor.StartAsync(firstConfiguration, CancellationToken.None);
        await WaitUntilAsync(
            () => control.State.GetState(control.Resource.Id) is ResourceLifecycle.Running,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        await supervisor.StartAsync(secondConfiguration, CancellationToken.None);
        await WaitUntilAsync(
            () => entries.Contexts.Count >= 2
                && control.State.GetState(control.Resource.Id) is ResourceLifecycle.Running,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        entries.Contexts.ShouldBe(new[] { first, second });
        entries.Hosts[0].StopCount.ShouldBe(1);
        entries.Hosts[0].DisposeCount.ShouldBe(1);
        entries.Hosts[0].DisposeContext.ShouldBeSameAs(first);
        second.BootstrapCredential.ToArray().ShouldBe("second"u8.ToArray());

        await supervisor.StopAsync(control, CancellationToken.None);
        await ((IHost)host.Value).StopAsync(CancellationToken.None);
        Directory.Delete(stateDirectory, recursive: true);
    }

    [Fact(DisplayName = DisplayPrefix + "replacement reconcile waits for readiness")]
    public async Task StartAsync_WithReplacementReadinessPending_ShouldWaitForRunning()
    {
        string stateDirectory = CreateTemporaryDirectory();
        var options = new InProcessGatewayOptions
        {
            ReadinessBudget = TimeSpan.FromSeconds(5),
            ProbeInterval = TimeSpan.FromMilliseconds(10),
            ProbeTimeout = TimeSpan.FromSeconds(1),
        };
        await using var host = new AsyncDisposableHost(
            new ProcessHost("Testing", stateDirectory));
        await ((IHost)host.Value).StartAsync(CancellationToken.None);
        var entries = new RecordingEntryInvoker();
        var probes = new BlockingReplacementReadinessProbeRunner();
        var supervisor = new InProcessMemberSupervisor(
            host.Value,
            options,
            probes,
            entries);
        TestControlContext control = TestControlContext.Create(
            stateDirectory,
            resourceName: "web");
        var firstConfiguration = TestMemberConfiguration(
            control,
            stateDirectory,
            TestAmbientContext(stateDirectory, "first"u8.ToArray()));
        var secondConfiguration = TestMemberConfiguration(
            control,
            stateDirectory,
            TestAmbientContext(stateDirectory, "second"u8.ToArray()));

        await supervisor.StartAsync(firstConfiguration, CancellationToken.None);
        await WaitUntilAsync(
            () => control.State.GetState(control.Resource.Id) is ResourceLifecycle.Running,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Task replacement = supervisor.StartAsync(secondConfiguration, CancellationToken.None);
        await probes.ReplacementReadinessEntered.WaitAsync(TimeSpan.FromSeconds(5));

        replacement.IsCompleted.ShouldBeFalse();
        control.State.GetState(control.Resource.Id).ShouldBe(ResourceLifecycle.Starting);

        probes.ReleaseReplacementReadiness();
        await replacement.WaitAsync(TimeSpan.FromSeconds(5));

        control.State.GetState(control.Resource.Id).ShouldBe(ResourceLifecycle.Running);
        entries.Contexts.Count.ShouldBe(2);
        await supervisor.StopAsync(control, CancellationToken.None);
        await ((IHost)host.Value).StopAsync(CancellationToken.None);
        Directory.Delete(stateDirectory, recursive: true);
    }

    [Fact(DisplayName = DisplayPrefix + "entry host surrender is bounded by readiness budget")]
    public async Task StartAsync_WhenEntryDoesNotSurrenderHost_ShouldFailWithinReadinessBudget()
    {
        string stateDirectory = CreateTemporaryDirectory();
        var options = new InProcessGatewayOptions
        {
            ReadinessBudget = TimeSpan.FromMilliseconds(100),
            ProbeInterval = TimeSpan.FromMilliseconds(10),
            ProbeTimeout = TimeSpan.FromSeconds(1),
        };
        await using var host = new AsyncDisposableHost(
            new ProcessHost("Testing", stateDirectory));
        await ((IHost)host.Value).StartAsync(CancellationToken.None);
        var entries = new DelayedHostReadyEntryInvoker();
        var supervisor = new InProcessMemberSupervisor(
            host.Value,
            options,
            new HealthyProbeRunner(),
            entries);
        TestControlContext control = TestControlContext.Create(
            stateDirectory,
            resourceName: "web");
        InProcessMemberConfiguration configuration = TestMemberConfiguration(
            control,
            stateDirectory,
            TestAmbientContext(stateDirectory, "credential"u8.ToArray()));

        await supervisor
            .StartAsync(configuration, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        control.State.GetState(control.Resource.Id).ShouldBe(ResourceLifecycle.Failed);

        entries.ReleaseHost();
        await WaitUntilAsync(
            () => entries.Host.DisposeCount == 1,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        entries.Host.DisposeContext.ShouldBeSameAs(configuration.ResourceContext);
        await ((IHost)host.Value).StopAsync(CancellationToken.None);
        Directory.Delete(stateDirectory, recursive: true);
    }

    [Fact(DisplayName = DisplayPrefix + "child startup is bounded by readiness budget")]
    public async Task StartAsync_WhenChildStartHangs_ShouldFailWithinReadinessBudget()
    {
        string stateDirectory = CreateTemporaryDirectory();
        var options = new InProcessGatewayOptions
        {
            ReadinessBudget = TimeSpan.FromMilliseconds(100),
            ProbeInterval = TimeSpan.FromMilliseconds(10),
            ProbeTimeout = TimeSpan.FromSeconds(1),
        };
        await using var host = new AsyncDisposableHost(
            new ProcessHost("Testing", stateDirectory));
        await ((IHost)host.Value).StartAsync(CancellationToken.None);
        var entries = new DelayedStartEntryInvoker();
        var supervisor = new InProcessMemberSupervisor(
            host.Value,
            options,
            new HealthyProbeRunner(),
            entries);
        TestControlContext control = TestControlContext.Create(
            stateDirectory,
            resourceName: "web");
        InProcessMemberConfiguration configuration = TestMemberConfiguration(
            control,
            stateDirectory,
            TestAmbientContext(stateDirectory, "credential"u8.ToArray()));

        Task start = supervisor.StartAsync(configuration, CancellationToken.None);
        await entries.Host.StartEntered.WaitAsync(TimeSpan.FromSeconds(5));
        await start.WaitAsync(TimeSpan.FromSeconds(5));

        control.State.GetState(control.Resource.Id).ShouldBe(ResourceLifecycle.Failed);

        entries.ReleaseStart();
        entries.CancelProgram();
        await WaitUntilAsync(
            () => entries.Host.DisposeCount == 1,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        entries.Host.DisposeContext.ShouldBeSameAs(configuration.ResourceContext);
        await ((IHost)host.Value).StopAsync(CancellationToken.None);
        Directory.Delete(stateDirectory, recursive: true);
    }

    [Fact(DisplayName = DisplayPrefix + "failed readiness stops and disposes the adopted generation")]
    public async Task Monitor_WhenReadinessFailsFast_ShouldReleaseGeneration()
    {
        string stateDirectory = CreateTemporaryDirectory();
        var options = new InProcessGatewayOptions
        {
            ProbeInterval = TimeSpan.FromMilliseconds(10),
            ProbeTimeout = TimeSpan.FromSeconds(1),
        };
        await using var host = new AsyncDisposableHost(
            new ProcessHost("Testing", stateDirectory));
        await ((IHost)host.Value).StartAsync(CancellationToken.None);
        var entries = new RecordingEntryInvoker();
        var supervisor = new InProcessMemberSupervisor(
            host.Value,
            options,
            new FailingReadinessProbeRunner(),
            entries);
        TestControlContext control = TestControlContext.Create(
            stateDirectory,
            resourceName: "web");
        InProcessMemberConfiguration configuration = TestMemberConfiguration(
            control,
            stateDirectory,
            TestAmbientContext(stateDirectory, "credential"u8.ToArray()));

        await supervisor.StartAsync(configuration, CancellationToken.None);
        await WaitUntilAsync(
            () => control.State.GetState(control.Resource.Id) is ResourceLifecycle.Failed
                && entries.Hosts[0].DisposeCount == 1,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        entries.Hosts[0].StopCount.ShouldBe(1);
        entries.Hosts[0].DisposeCount.ShouldBe(1);
        entries.Hosts[0].DisposeContext.ShouldBeSameAs(configuration.ResourceContext);
        await supervisor.StopAsync(control, CancellationToken.None);
        await ((IHost)host.Value).StopAsync(CancellationToken.None);
        Directory.Delete(stateDirectory, recursive: true);
    }

    private static ResourcePlan TestPlan(
        string resource,
        WorkloadKind workload = WorkloadKind.Deployment,
        IReadOnlyList<ProbeMapping>? probes = null,
        bool mount = false,
        bool exposure = false,
        string exposureProtocol = "tcp",
        IReadOnlyDictionary<string, string>? environment = null)
    {
        MountBinding[] mounts = mount
            ? [new MountBinding("config", "/config", ResourceMountKind.Configuration, "literal:value")]
            : [];
        return new ResourcePlan(
            ResourcePlan.CurrentSchema,
            (ResourceName)resource,
            "Web",
            new WorkloadSpec(
                workload,
                1,
                StableIdentity: false,
                ReadinessGate.For(workload),
                StopGraceSeconds: 5,
                RestartPolicy: "OnFailure"),
            new ContainerSpec(
                resource,
                ArtifactRef.Self,
                [new PortBinding("http", 8080, "tcp", "http")],
                mounts,
                environment ?? new Dictionary<string, string>(),
                probes ?? [new ProbeMapping("readiness", null, ProbeKind.None, null, [])]),
            [],
            [new ServiceSpec("http", "http", 8080, "tcp", Headless: false, Governing: false)],
            exposure
                ? [new ExposureSpec("public-http", "http", "http", "http", exposureProtocol, 8080)]
                : [],
            new Dictionary<string, string>(),
            new ControlPlaneSpec("http", "/cohesion/v1"));
    }

    private static ResourceManifest TestManifest(
        string resource,
        bool mount,
        bool publicEndpoint = false)
    {
        return new ResourceManifest
        {
            Name = (ResourceName)resource,
            Kind = "Web",
            Application = (ApplicationName)"app",
            ApplicationModel = "Tests.Web.ApplicationModel",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "Tests.Web",
                Composable = true,
                Project = Path.Combine(Path.GetTempPath(), "Tests.Web.csproj"),
            },
            Endpoints =
            [
                new ResourceManifestEndpoint
                {
                    Name = "http",
                    Scheme = "http",
                    Protocol = "tcp",
                    ContainerPort = 8080,
                    Public = publicEndpoint,
                },
            ],
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = "http",
                Path = "/cohesion/v1",
            },
            Mounts = mount
                ?
                [
                    new ResourceManifestMount
                    {
                        Name = "config",
                        ContainerPath = "/config",
                        Kind = ResourceMountKind.Configuration,
                        Source = "literal:value",
                    },
                ]
                : [],
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = WorkloadKind.Deployment,
                StopGraceSeconds = 5,
            },
        };
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "cohesion-inprocess-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static InProcessProbeConfiguration ExplicitProbe(string role) =>
        new(
            new ProbeMapping(role, null, ProbeKind.None, null, []),
            IsDefaultControlPlane: false);

    private static InProcessGatewayOptions FastRestartOptions() =>
        new()
        {
            ProbeInterval = TimeSpan.FromMilliseconds(10),
            ProbeTimeout = TimeSpan.FromSeconds(1),
            InitialRestartBackoff = TimeSpan.Zero,
            MaximumRestartBackoff = TimeSpan.Zero,
            MaximumRestartAttempts = 5,
        };

    private static ResourceContext TestAmbientContext(
        string contentRoot,
        ReadOnlyMemory<byte> credential) =>
        new(
            applicationName: "app",
            resourceName: "web",
            environmentName: "Testing",
            gatewayName: "inprocess",
            contentRootPath: contentRoot,
            endpoints: new Dictionary<string, Uri>
            {
                ["http"] = new Uri("http://127.0.0.1:43123/"),
            },
            bootstrapCredential: credential);

    private static InProcessMemberConfiguration TestMemberConfiguration(
        TestControlContext control,
        string contentRoot,
        ResourceContext ambient,
        RestartPolicy restartPolicy = RestartPolicy.OnFailure) =>
        new(
            control,
            new InProcessResourceArtifact(
                control.Resource.Id,
                Assembly.GetExecutingAssembly(),
                contentRoot),
            ambient,
            [new ResourceEndpoint("http", "http", 43123, Host: "127.0.0.1")],
            ExplicitProbe("startup"),
            ExplicitProbe("readiness"),
            ExplicitProbe("liveness"),
            restartPolicy);

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected in-process condition was not observed.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    private sealed class PlainResource : IApplicationResource
    {
        internal PlainResource(string name) => Name = (ResourceName)name;

        public ResourceName Name { get; }
    }

    private sealed class ManifestResource : PlannedResource
    {
        internal ManifestResource(ResourceManifest manifest)
            : base(manifest)
        {
        }
    }

    private sealed class TestDescriptor : IApplicationResourceDescriptor
    {
        internal TestDescriptor(IApplicationResource resource, ResourcePlan plan)
        {
            Resource = resource;
            Plan = plan;
        }

        public IApplicationResource Resource { get; }

        public ResourcePlan? Plan { get; }

        public IReadOnlyList<IApplicationResourceDescriptor> Dependencies { get; } = [];

        public IApplicationResourceDescriptor DependsOn(IApplicationResourceDescriptor resource) => this;

        public IApplicationResourceDescriptor DependsOn(params IApplicationResourceDescriptor[] resources) => this;
    }

    private sealed class TestApplicationModel : IApplicationModel
    {
        internal TestApplicationModel(IApplicationResourceDescriptor descriptor, ResourceManifest manifest)
        {
            Descriptors = [descriptor];
            Resources = [descriptor.Resource];
            Manifests = [manifest];
            Plans = [descriptor.Plan!];
        }

        public ApplicationName Name => (ApplicationName)"app";

        public IApplicationEnvironment Environment { get; } = new TestEnvironment();

        public GatewayRunMode RunMode => GatewayRunMode.Run;

        public ResourceName GatewayIdentity => (ResourceName)"inprocess";

        public string Owner => "app@inprocess";

        public bool Adopt => false;

        public bool RestartOrphans => false;

        public IReadOnlyList<IApplicationResourceDescriptor> Descriptors { get; }

        public IReadOnlyList<IApplicationResource> Resources { get; }

        public IReadOnlyList<ResourceManifest> Manifests { get; }

        public IReadOnlyList<ResourcePlan> Plans { get; }
    }

    private sealed class TestEnvironment : IApplicationEnvironment
    {
        public EnvironmentName Name => (EnvironmentName)"Testing";

        public bool IsLocal => false;

        public bool IsDevelopment => false;
    }

    private sealed class TestControlContext : IResourceControlContext
    {
        private readonly IResourceArtifact _artifact;

        private TestControlContext(
            IApplicationResourceDescriptor descriptor,
            IApplicationModel model,
            IApplicationResourceStateManager state,
            IResourceArtifact artifact,
            ResourceInputs inputs,
            IReadOnlyList<ResourceDependencyObservation> observations)
        {
            Descriptor = descriptor;
            Model = model;
            State = state;
            _artifact = artifact;
            Inputs = inputs;
            ObservedDependencies = observations;
            State.StateChanged += (_, args) =>
            {
                States.Add(args.Current);
                LastDetail = args.Detail;
            };
        }

        public IApplicationResourceDescriptor Descriptor { get; }

        public IApplicationResource Resource => Descriptor.Resource;

        public ResourcePlan Plan => Descriptor.Plan!;

        public IApplicationModel Model { get; }

        public IApplicationResourceStateManager State { get; }

        public IReadOnlyList<IApplicationResource> Dependencies { get; } = [];

        public ResourceInputs Inputs { get; }

        public IReadOnlyList<ResourceDependencyObservation> ObservedDependencies { get; }

        internal List<ResourceLifecycle> States { get; } = [];

        internal string? LastDetail { get; private set; }

        public T GetArtifact<T>() where T : class, IResourceArtifact => (T)_artifact;

        internal static TestControlContext Create(
            string contentRoot,
            string resourceName,
            bool mount = false,
            IReadOnlyList<ResourceDependencyObservation>? observations = null,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            ResourceManifest manifest = TestManifest(resourceName, mount);
            var resource = new ManifestResource(manifest);
            ResourcePlan plan = TestPlan(resourceName, mount: mount, environment: environment);
            var descriptor = new TestDescriptor(resource, plan);
            var model = new TestApplicationModel(descriptor, manifest);
            var state = new InMemoryResourceStateManager();
            var artifact = new InProcessResourceArtifact(
                ((IApplicationResource)resource).Id,
                Assembly.GetExecutingAssembly(),
                contentRoot);
            var inputs = new ResourceInputs(
                mount
                    ? new Dictionary<string, ResourceMountInput>
                    {
                        ["config"] = ResourceMountInput.Resolved("literal:value", "value"u8.ToArray()),
                    }
                    : new Dictionary<string, ResourceMountInput>(),
                "credential"u8.ToArray(),
                "trust-key"u8.ToArray());
            return new TestControlContext(
                descriptor,
                model,
                state,
                artifact,
                inputs,
                observations ?? []);
        }
    }

    private sealed class NoOpSupervisor : IInProcessMemberSupervisor
    {
        public Task StartAsync(InProcessMemberConfiguration configuration, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task StopAsync(IResourceControlContext context, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class RestartProbeRunner : IInProcessProbeRunner
    {
        private int _liveness;

        public Task<InProcessProbeResult> RunAsync(
            InProcessProbeConfiguration probe,
            ResourceContext context,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(
                probe.Mapping.Role,
                "liveness",
                StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(InProcessProbeResult.Success("ready"));
            }

            int attempt = Interlocked.Increment(ref _liveness);
            return Task.FromResult(attempt <= 3
                ? InProcessProbeResult.Failure($"failure {attempt}")
                : InProcessProbeResult.Success("live"));
        }
    }

    private sealed class HealthyProbeRunner : IInProcessProbeRunner
    {
        public Task<InProcessProbeResult> RunAsync(
            InProcessProbeConfiguration probe,
            ResourceContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(InProcessProbeResult.Success("healthy"));
    }

    private sealed class FailingReadinessProbeRunner : IInProcessProbeRunner
    {
        public Task<InProcessProbeResult> RunAsync(
            InProcessProbeConfiguration probe,
            ResourceContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(string.Equals(
                probe.Mapping.Role,
                "readiness",
                StringComparison.OrdinalIgnoreCase)
                    ? InProcessProbeResult.Failure("readiness failed", failFast: true)
                    : InProcessProbeResult.Success("healthy"));
    }

    private sealed class BlockingReplacementReadinessProbeRunner : IInProcessProbeRunner
    {
        private readonly TaskCompletionSource _replacementReadinessEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseReplacementReadiness = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readinessAttempts;

        internal Task ReplacementReadinessEntered => _replacementReadinessEntered.Task;

        internal void ReleaseReplacementReadiness() => _releaseReplacementReadiness.TrySetResult();

        public async Task<InProcessProbeResult> RunAsync(
            InProcessProbeConfiguration probe,
            ResourceContext context,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(
                probe.Mapping.Role,
                "readiness",
                StringComparison.OrdinalIgnoreCase)
                || Interlocked.Increment(ref _readinessAttempts) == 1)
            {
                return InProcessProbeResult.Success("healthy");
            }

            _replacementReadinessEntered.TrySetResult();
            await _releaseReplacementReadiness.Task.WaitAsync(cancellationToken);
            return InProcessProbeResult.Success("healthy");
        }
    }

    private sealed class RecordingEntryInvoker : IResourceEntryInvoker
    {
        private readonly Lock _lock = new();

        internal List<ResourceContext> Contexts { get; } = [];

        internal List<TestMemberHost> Hosts { get; } = [];

        internal List<TaskCompletionSource> Completions { get; } = [];

        public IResourceEntryInvocation Invoke(
            InProcessResourceArtifact artifact,
            ResourceContext context)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var host = new TestMemberHost(completion);
            lock (_lock)
            {
                Contexts.Add(context);
                Hosts.Add(host);
                Completions.Add(completion);
            }
            return new TestInvocation(Task.FromResult<IHost>(host), completion.Task);
        }
    }

    private sealed class InitialFailureEntryInvoker : IResourceEntryInvoker
    {
        private readonly Queue<Exception> _failures;
        private readonly Lock _lock = new();

        internal InitialFailureEntryInvoker(params Exception[] failures)
        {
            _failures = new Queue<Exception>(failures);
        }

        internal int InvocationCount { get; private set; }

        public IResourceEntryInvocation Invoke(
            InProcessResourceArtifact artifact,
            ResourceContext context)
        {
            lock (_lock)
            {
                InvocationCount++;
                if (_failures.TryDequeue(out Exception? failure))
                {
                    return new TestInvocation(
                        Task.FromException<IHost>(failure),
                        Task.FromException(failure));
                }
            }

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var host = new TestMemberHost(completion);
            return new TestInvocation(Task.FromResult<IHost>(host), completion.Task);
        }
    }

    private sealed class DelayedHostReadyEntryInvoker : IResourceEntryInvoker
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IHost> _hostReady = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal DelayedHostReadyEntryInvoker()
        {
            Host = new TestMemberHost(_completion);
        }

        internal TestMemberHost Host { get; }

        public IResourceEntryInvocation Invoke(
            InProcessResourceArtifact artifact,
            ResourceContext context) =>
            new TestInvocation(_hostReady.Task, _completion.Task);

        internal void ReleaseHost() => _hostReady.TrySetResult(Host);
    }

    private sealed class DelayedStartEntryInvoker : IResourceEntryInvoker
    {
        private readonly TaskCompletionSource _programCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseStart = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal DelayedStartEntryInvoker()
        {
            Host = new DelayedStartMemberHost(_releaseStart.Task);
        }

        internal DelayedStartMemberHost Host { get; }

        public IResourceEntryInvocation Invoke(
            InProcessResourceArtifact artifact,
            ResourceContext context) =>
            new TestInvocation(Task.FromResult<IHost>(Host), _programCompletion.Task);

        internal void ReleaseStart() => _releaseStart.TrySetResult();

        internal void CancelProgram() => _programCompletion.TrySetCanceled();
    }

    private sealed record TestInvocation(
        Task<IHost> HostReady,
        Task Completion) : IResourceEntryInvocation;

    private sealed class FailingStopMemberHost : Host<TestMemberContext>
    {
        private readonly TestMemberContext _context = new();

        internal FailingStopMemberHost()
            : base(new TestMemberOptions())
        {
        }

        public override TestMemberContext Context => _context;

        internal int DisposeCount { get; private set; }

        internal ResourceContext? DisposeContext { get; private set; }

        protected override Task OnStoppingAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("member stop failed"));

        protected override async ValueTask DisposeAsync(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
                DisposeContext = ResourceRuntime.Current;
            }
            await base.DisposeAsync(disposing);
        }
    }

    private sealed class DelayedStartMemberHost : Host<TestMemberContext>
    {
        private readonly TestMemberContext _context;

        internal DelayedStartMemberHost(Task releaseStart)
            : base(new TestMemberOptions())
        {
            var service = new DelayedStartService(releaseStart);
            _context = new TestMemberContext(service);
            StartEntered = service.StartEntered;
        }

        public override TestMemberContext Context => _context;

        internal Task StartEntered { get; }

        internal int DisposeCount { get; private set; }

        internal ResourceContext? DisposeContext { get; private set; }

        protected override async ValueTask DisposeAsync(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
                DisposeContext = ResourceRuntime.Current;
            }
            await base.DisposeAsync(disposing);
        }
    }

    private sealed class DelayedStartService : IHostService
    {
        private readonly Task _releaseStart;
        private readonly TaskCompletionSource _startEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal DelayedStartService(Task releaseStart)
        {
            _releaseStart = releaseStart;
        }

        public ServiceId Id { get; } = ServiceId.New();

        internal Task StartEntered => _startEntered.Task;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            _startEntered.TrySetResult();
            await _releaseStart.ConfigureAwait(false);
        }

        public Task StopAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class TestMemberHost : Host<TestMemberContext>
    {
        private readonly TaskCompletionSource _completion;
        private readonly TestMemberContext _context = new();

        internal TestMemberHost(TaskCompletionSource completion)
            : base(new TestMemberOptions())
        {
            _completion = completion;
        }

        public override TestMemberContext Context => _context;

        internal int StopCount { get; private set; }

        internal int DisposeCount { get; private set; }

        internal ResourceContext? DisposeContext { get; private set; }

        protected override Task OnStoppedAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            _completion.TrySetResult();
            return Task.CompletedTask;
        }

        protected override async ValueTask DisposeAsync(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
                DisposeContext = ResourceRuntime.Current;
            }
            await base.DisposeAsync(disposing);
        }
    }

    private sealed class TestMemberContext : HostContext
    {
        private readonly IReadOnlyList<IHostService> _hostedServices;

        internal TestMemberContext(params IHostService[] hostedServices)
        {
            _hostedServices = hostedServices;
        }

        public override IHostEnvironment Environment { get; } = new HostEnvironment("Testing");

        public override IEnumerable<IHostService> HostedServices => _hostedServices;
    }

    private sealed class TestMemberOptions : HostOptions<TestMemberContext>;

    private sealed class DisposableGateway : IDisposable
    {
        internal DisposableGateway(InProcessGateway value) => Value = value;

        internal InProcessGateway Value { get; }

        public void Dispose()
        {
            ((IHost)Value.Host).Dispose();
        }
    }

    private sealed class AsyncDisposableHost : IAsyncDisposable
    {
        internal AsyncDisposableHost(ProcessHost value) => Value = value;

        internal ProcessHost Value { get; }

        public ValueTask DisposeAsync() => ((IHost)Value).DisposeAsync();
    }
}
