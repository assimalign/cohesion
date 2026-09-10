using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public class ApplicationGatewayTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - StartAsync: Should reconcile in dependency order")]
    public async Task StartAsync_ProvisionsInDependencyOrder()
    {
        var reconciled = new List<string>();
        var deleted = new List<string>();
        var state = new InMemoryResourceStateManager();
        var gateway = new TestGateway(state, new[] { new RecordingController(reconciled, deleted) });

        IApplicationModel model = BuildChain(gateway);

        await ((IApplicationGateway)gateway).StartAsync(model);

        reconciled.ShouldBe(new[] { "a", "b", "c" });
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Stop and Uninstall: Should separate runtime stop from destructive teardown")]
    public async Task StopAsync_ThenUninstallAsync_UsesDistinctControllerHooksInReverseOrder()
    {
        var reconciled = new List<string>();
        var deleted = new List<string>();
        var stopped = new List<string>();
        var state = new InMemoryResourceStateManager();
        var gateway = new TestGateway(
            state,
            new[] { new RecordingController(reconciled, deleted, stopped: stopped) });

        IApplicationModel model = BuildChain(gateway);
        IApplicationGateway control = gateway;

        await control.StartAsync(model);
        await control.StopAsync();

        stopped.ShouldBe(new[] { "c", "b", "a" });
        deleted.ShouldBeEmpty();

        await control.UninstallAsync(model);

        deleted.ShouldBe(new[] { "c", "b", "a" });
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - StartAsync: Should block dependents when readiness fails")]
    public async Task StartAsync_WhenDependencyFails_BlocksDependentsAndThrows()
    {
        var reconciled = new List<string>();
        var deleted = new List<string>();
        var state = new InMemoryResourceStateManager();
        var failing = new HashSet<string> { "b" };
        var gateway = new TestGateway(state, new[] { new RecordingController(reconciled, deleted, failing) });

        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(gateway);
        IApplicationResourceDescriptor a = builder.AddResource(new TestResource("a"));
        IApplicationResourceDescriptor b = builder.AddResource(new TestResource("b"));
        b.DependsOn(a);
        IApplicationResourceDescriptor c = builder.AddResource(new TestResource("c"));
        c.DependsOn(b);
        IApplicationModel model = builder.Build().Model;

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await ((IApplicationGateway)gateway).StartAsync(model));

        reconciled.ShouldBe(new[] { "a", "b" });   // c is never reconciled
        state.GetState(c.Resource.Id).ShouldBe(ResourceLifecycle.Blocked);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - StartAsync: Should fail when a readiness budget expires")]
    public async Task StartAsync_WhenResourceNeverBecomesReady_TimesOutAndThrows()
    {
        var reconciled = new List<string>();
        var deleted = new List<string>();
        var state = new InMemoryResourceStateManager();
        var controller = new RecordingController(reconciled, deleted, leaveStarting: true);
        var gateway = new TestGateway(state, new[] { controller }, readinessBudget: TimeSpan.FromMilliseconds(75));

        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(gateway);
        builder.AddResource(new TestResource("a"));
        IApplicationModel model = builder.Build().Model;

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await ((IApplicationGateway)gateway).StartAsync(model));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - StartAsync: Should fail when a resource stops before Running")]
    public async Task StartAsync_WhenResourceStopsBeforeRunning_ShouldFailAndBlockDependents()
    {
        // Arrange
        var reconciled = new List<string>();
        var deleted = new List<string>();
        var state = new InMemoryResourceStateManager();
        var observedStates = new Dictionary<string, ResourceLifecycle>
        {
            ["a"] = ResourceLifecycle.Stopped,
        };
        var controller = new RecordingController(reconciled, deleted, observedStates: observedStates);
        var gateway = new TestGateway(state, new[] { controller });
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(gateway);
        IApplicationResourceDescriptor a = builder.AddResource(new TestResource("a"));
        IApplicationResourceDescriptor b = builder.AddResource(new TestResource("b"));
        b.DependsOn(a);
        IApplicationModel model = builder.Build().Model;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            async () => await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token));

        // Assert
        exception.Message.ShouldContain("'Stopped'");
        cancellation.IsCancellationRequested.ShouldBeFalse();
        reconciled.ShouldBe(new[] { "a" });
        state.GetState(b.Resource.Id).ShouldBe(ResourceLifecycle.Blocked);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - StartAsync: Should not re-gate a dependent after initial readiness degrades")]
    public async Task StartAsync_WhenDependencyDegradesAfterRunning_ShouldNotRegateDependent()
    {
        // Arrange
        var reconciled = new List<string>();
        var deleted = new List<string>();
        var state = new InMemoryResourceStateManager();
        var observedStates = new Dictionary<string, ResourceLifecycle>
        {
            ["a"] = ResourceLifecycle.Degraded,
        };
        var controller = new RecordingController(reconciled, deleted, observedStates: observedStates);
        var gateway = new TestGateway(state, new[] { controller });
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(gateway);
        IApplicationResourceDescriptor a = builder.AddResource(new TestResource("a"));
        IApplicationResourceDescriptor b = builder.AddResource(new TestResource("b"));
        b.DependsOn(a);
        IApplicationModel model = builder.Build().Model;
        ResourceStateChangedEventArgs? degradation = null;
        state.StateChanged += (_, args) =>
        {
            if (args.Resource == a.Resource.Id && args.Current == ResourceLifecycle.Degraded)
            {
                degradation = args;
            }
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Act
        Task start = ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);
        ResourceLifecycle initial = await state.WaitForStateAsync(
            a.Resource.Id,
            new HashSet<ResourceLifecycle> { ResourceLifecycle.Degraded },
            TimeSpan.FromSeconds(1),
            cancellation.Token);

        // Assert
        initial.ShouldBe(ResourceLifecycle.Degraded);
        reconciled.ShouldBe(new[] { "a" });

        state.SetState(a.Resource.Id, ResourceLifecycle.Running);
        await start.WaitAsync(TimeSpan.FromSeconds(1));
        reconciled.ShouldBe(new[] { "a", "b" });

        state.SetState(a.Resource.Id, ResourceLifecycle.Degraded, "Liveness probe failed.");
        degradation.ShouldNotBeNull();
        degradation!.Previous.ShouldBe(ResourceLifecycle.Running);
        state.GetState(a.Resource.Id).ShouldBe(ResourceLifecycle.Degraded);
        state.GetState(b.Resource.Id).ShouldBe(ResourceLifecycle.Running);

        await ((IApplicationGateway)gateway).StopAsync(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - StartAsync: Should propagate readiness cancellation")]
    public async Task StartAsync_WhenReadinessWaitIsCanceled_ShouldPropagateOperationCanceledException()
    {
        // Arrange
        var reconciled = new List<string>();
        var deleted = new List<string>();
        var stopped = new List<string>();
        var reconciledSignal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new InMemoryResourceStateManager();
        var controller = new RecordingController(
            reconciled,
            deleted,
            leaveStarting: true,
            stopped: stopped,
            reconciledSignal: reconciledSignal);
        var gateway = new TestGateway(state, new[] { controller });
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(gateway);
        builder.AddResource(new TestResource("a"));
        IApplicationModel model = builder.Build().Model;
        using var cancellation = new CancellationTokenSource();
        Task start = ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);
        await reconciledSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Act
        cancellation.Cancel();

        // Assert
        OperationCanceledException exception = await Should.ThrowAsync<OperationCanceledException>(
            async () => await start.WaitAsync(TimeSpan.FromSeconds(2)));
        exception.CancellationToken.ShouldBe(cancellation.Token);
        reconciled.ShouldBe(new[] { "a" });
        stopped.ShouldBe(new[] { "a" });
        deleted.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Cancellation: Should propagate caller tokens to every controller lifecycle hook")]
    public async Task ControllerHooks_ShouldReceiveCallerCancellationTokens()
    {
        // Arrange
        var state = new InMemoryResourceStateManager();
        var controller = new TokenRecordingController();
        var gateway = new TestGateway(state, new[] { controller });
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(gateway);
        builder.AddResource(new TestResource("worker"));
        IApplicationModel model = builder.Build().Model;
        using var startCancellation = new CancellationTokenSource();
        using var stopCancellation = new CancellationTokenSource();
        using var uninstallCancellation = new CancellationTokenSource();

        // Act
        await ((IApplicationGateway)gateway).StartAsync(model, startCancellation.Token);
        await ((IApplicationGateway)gateway).StopAsync(stopCancellation.Token);
        await ((IApplicationGateway)gateway).UninstallAsync(model, uninstallCancellation.Token);

        // Assert
        controller.ReconcileToken.ShouldBe(startCancellation.Token);
        controller.StopToken.ShouldBe(stopCancellation.Token);
        controller.DeleteToken.ShouldBe(uninstallCancellation.Token);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Plan gate: Should satisfy each workload on its declared state")]
    [InlineData(WorkloadKind.Deployment, ResourceLifecycle.Running)]
    [InlineData(WorkloadKind.StatefulSet, ResourceLifecycle.Running)]
    [InlineData(WorkloadKind.DaemonSet, ResourceLifecycle.Running)]
    [InlineData(WorkloadKind.Job, ResourceLifecycle.Stopped)]
    public async Task StartAsync_PlanGate_ShouldHonorWorkloadSatisfyingState(
        WorkloadKind workload,
        ResourceLifecycle satisfyingState)
    {
        // Arrange
        var reconciled = new List<string>();
        var deleted = new List<string>();
        var state = new InMemoryResourceStateManager();
        var observedStates = new Dictionary<string, ResourceLifecycle>
        {
            ["worker"] = satisfyingState,
        };
        var gateway = new TestGateway(
            state,
            new[] { new RecordingController(reconciled, deleted, observedStates: observedStates) });
        IApplicationModel model = BuildWorkload(gateway, workload);

        // Act
        await ((IApplicationGateway)gateway).StartAsync(model);

        // Assert
        reconciled.ShouldBe(new[] { "worker" });
        state.GetState(model.Resources[0].Id).ShouldBe(satisfyingState);
        model.Descriptors[0].Plan!.Workload.Gate.Satisfying.ShouldContain(satisfyingState);

        await ((IApplicationGateway)gateway).StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Validate: Should refuse an unrealizable plan before gathering")]
    public void Build_WhenControllerCannotRealizePlan_ShouldNameResourceAndGatewayBeforeGathering()
    {
        // Arrange
        var state = new InMemoryResourceStateManager();
        var gateway = new TestGateway(
            state,
            new IApplicationResourceController[] { new RejectingController() });
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                [])
            .UseGateway(gateway);
        builder.AddResource(CreateManifest("worker", WorkloadKind.Deployment));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => builder.Build());

        // Assert
        exception.Message.ShouldContain("worker");
        exception.Message.ShouldContain("test");
        exception.Message.ShouldContain("Kubernetes scheduling spec");
        gateway.Gathered.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local validation: Should reject a plan outside the local compiler subset")]
    public void Build_WhenLocalPlanRequestsMultipleReplicas_ShouldFailValidation()
    {
        // Arrange
        var gateway = new LocalGateway();
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                [])
            .UseGateway(gateway);
        builder.AddResource(CreateManifest("worker", WorkloadKind.Deployment, replicas: 2));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => builder.Build());

        // Assert
        exception.Message.ShouldContain("worker");
        exception.Message.ShouldContain("local");
        exception.Message.ShouldContain("exactly one process");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - ResolveInputsAsync: Should run after dependencies and on every reconcile")]
    public async Task ReconcileAsync_ShouldResolveInputsAfterDependenciesOnEveryPass()
    {
        // Arrange
        var events = new List<string>();
        var state = new InMemoryResourceStateManager();
        var controller = new SequenceController(events);
        var gateway = new TestGateway(
            state,
            new[] { controller },
            inputResolver: (descriptor, context, _) =>
            {
                string dependencyState = descriptor.Dependencies.Count == 0
                    ? "none"
                    : context.State.GetState(descriptor.Dependencies[0].Resource.Id).ToString();
                events.Add($"inputs:{descriptor.Resource.Name}:{dependencyState}");
                return ValueTask.FromResult(ResourceInputs.Empty);
            });
        IApplicationModel model = BuildChain(gateway);
        IApplicationGateway control = gateway;

        // Act
        await control.StartAsync(model);
        await control.ReconcileAsync(model);

        // Assert
        events.ShouldBe(
        [
            "inputs:a:none",
            "controller:a",
            "inputs:b:Running",
            "controller:b",
            "inputs:c:Running",
            "controller:c",
            "inputs:a:none",
            "controller:a",
            "inputs:b:Running",
            "controller:b",
            "inputs:c:Running",
            "controller:c",
        ]);

        await control.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - ResolveInputsAsync: Should resolve literal and parameter sources and type unresolved sources")]
    public async Task StartAsync_DefaultInputResolver_ShouldReturnResolvedAndUnresolvedMountInputs()
    {
        // Arrange
        IReadOnlyList<ResourceManifestMount> mounts =
        [
            new ResourceManifestMount
            {
                Name = "settings",
                ContainerPath = "/inputs/settings",
                Kind = ResourceMountKind.Configuration,
                Source = "literal:hello",
            },
            new ResourceManifestMount
            {
                Name = "token",
                ContainerPath = "/inputs/token",
                Kind = ResourceMountKind.Secret,
                Source = "parameter:api-token",
            },
            new ResourceManifestMount
            {
                Name = "external",
                ContainerPath = "/inputs/external",
                Kind = ResourceMountKind.Configuration,
                Source = "configuration-store:feature",
            },
        ];
        var options = new ApplicationGatewayOptions();
        options.Parameters.Add("api-token", "s3cret");
        var state = new InMemoryResourceStateManager();
        var controller = new InputCapturingController();
        var gateway = new TestGateway(state, new[] { controller }, options: options);
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                [])
            .UseGateway(gateway);
        builder.AddResource(CreateManifest("worker", WorkloadKind.Deployment, mounts: mounts));
        IApplicationModel model = builder.Build().Model;

        // Act
        await ((IApplicationGateway)gateway).StartAsync(model);

        // Assert
        ResourceInputs inputs = controller.Inputs.ShouldNotBeNull();
        Encoding.UTF8.GetString(inputs.Mounts["settings"].Content.Span).ShouldBe("hello");
        Encoding.UTF8.GetString(inputs.Mounts["token"].Content.Span).ShouldBe("s3cret");
        inputs.Mounts["external"].IsResolved.ShouldBeFalse();
        string unresolvedReason = inputs.Mounts["external"].UnresolvedReason.ShouldNotBeNull();
        unresolvedReason.ShouldContain("declared dependency", Case.Insensitive);
        inputs.BootstrapCredential.IsEmpty.ShouldBeFalse();
        inputs.ApplicationTrustKey.IsEmpty.ShouldBeFalse();

        await ((IApplicationGateway)gateway).StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Controllers: Should prefer registered overrides to the platform controller")]
    public async Task StartAsync_WithRegisteredController_ShouldUseRegisteredControllerFirst()
    {
        // Arrange
        var registeredCalls = new List<string>();
        var platformCalls = new List<string>();
        var deleted = new List<string>();
        var state = new InMemoryResourceStateManager();
        var options = new ApplicationGatewayOptions();
        options.Controllers.Add(new RecordingController(registeredCalls, deleted));
        var gateway = new TestGateway(
            state,
            new[] { new RecordingController(platformCalls, deleted) },
            options: options);
        IApplicationModel model = BuildChain(gateway);

        // Act
        await ((IApplicationGateway)gateway).StartAsync(model);

        // Assert
        registeredCalls.ShouldBe(new[] { "a", "b", "c" });
        platformCalls.ShouldBeEmpty();

        await ((IApplicationGateway)gateway).StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Reconcile: Should continue independent resources and mark skipped dependents")]
    public async Task StartAsync_WhenControllerSkipsResource_ShouldSkipItsDependentsAndContinue()
    {
        // Arrange
        var reconciled = new List<string>();
        var deleted = new List<string>();
        var state = new InMemoryResourceStateManager();
        var observedStates = new Dictionary<string, ResourceLifecycle>
        {
            ["a"] = ResourceLifecycle.Skipped,
            ["c"] = ResourceLifecycle.Running,
        };
        var gateway = new TestGateway(
            state,
            new[] { new RecordingController(reconciled, deleted, observedStates: observedStates) });
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(gateway);
        IApplicationResourceDescriptor a = builder.AddResource(new TestResource("a"));
        IApplicationResourceDescriptor b = builder.AddResource(new TestResource("b"));
        b.DependsOn(a);
        builder.AddResource(new TestResource("c"));
        IApplicationModel model = builder.Build().Model;

        // Act
        await ((IApplicationGateway)gateway).StartAsync(model);

        // Assert
        reconciled.ShouldBe(new[] { "a", "c" });
        state.GetState(b.Resource.Id).ShouldBe(ResourceLifecycle.Skipped);
        state.GetState(model.Resources[2].Id).ShouldBe(ResourceLifecycle.Running);

        await ((IApplicationGateway)gateway).StopAsync();
    }

    private static IApplicationModel BuildChain(IApplicationGateway gateway)
    {
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(gateway);
        IApplicationResourceDescriptor a = builder.AddResource(new TestResource("a"));
        IApplicationResourceDescriptor b = builder.AddResource(new TestResource("b"));
        b.DependsOn(a);
        IApplicationResourceDescriptor c = builder.AddResource(new TestResource("c"));
        c.DependsOn(b);
        return builder.Build().Model;
    }

    private static IApplicationModel BuildWorkload(
        IApplicationGateway gateway,
        WorkloadKind workload)
    {
        IReadOnlyList<ResourceManifestMount>? mounts = workload is WorkloadKind.StatefulSet
            ?
            [
                new ResourceManifestMount
                {
                    Name = "data",
                    ContainerPath = "/data",
                    Kind = ResourceMountKind.Volume,
                    Size = "1Gi",
                },
            ]
            : null;
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                [])
            .UseGateway(gateway);
        builder.AddResource(CreateManifest("worker", workload, mounts: mounts));
        return builder.Build().Model;
    }

    private static ResourceManifest CreateManifest(
        string name,
        WorkloadKind workload,
        int replicas = 1,
        IReadOnlyList<ResourceManifestMount>? mounts = null) =>
        new()
        {
            Name = name,
            Application = "appa",
            Kind = "test",
            ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "test.dll",
                AppHost = "test",
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
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = "http",
                Path = "/cohesion/v1",
            },
            Mounts = mounts ?? Array.Empty<ResourceManifestMount>(),
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = workload,
                Replicas = replicas,
                RestartPolicy = "Never",
            },
        };

    private sealed class SequenceController : IApplicationResourceController
    {
        private readonly ICollection<string> _events;

        public SequenceController(ICollection<string> events)
        {
            _events = events;
        }

        public bool CanRealize(ResourcePlan plan, out string? reason)
        {
            reason = null;
            return true;
        }

        public Task ReconcileAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Inputs.ShouldNotBeNull();
            _events.Add($"controller:{context.Resource.Name}");
            context.State.SetState(context.Resource.Id, ResourceLifecycle.Running);
            return Task.CompletedTask;
        }

        public Task StopAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RejectingController : IApplicationResourceController
    {
        public bool CanRealize(ResourcePlan plan, out string? reason)
        {
            reason = "Kubernetes scheduling spec is required.";
            return false;
        }

        public Task ReconcileAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Rejected plans must not be reconciled.");

        public Task StopAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class InputCapturingController : IApplicationResourceController
    {
        public ResourceInputs? Inputs { get; private set; }

        public bool CanRealize(ResourcePlan plan, out string? reason)
        {
            reason = null;
            return true;
        }

        public Task ReconcileAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default)
        {
            Inputs = context.Inputs;
            context.State.SetState(context.Resource.Id, ResourceLifecycle.Running);
            return Task.CompletedTask;
        }

        public Task StopAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TokenRecordingController : IApplicationResourceController
    {
        public CancellationToken ReconcileToken { get; private set; }

        public CancellationToken StopToken { get; private set; }

        public CancellationToken DeleteToken { get; private set; }

        public bool CanRealize(ResourcePlan plan, out string? reason)
        {
            reason = null;
            return true;
        }

        public Task ReconcileAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default)
        {
            ReconcileToken = cancellationToken;
            context.State.SetState(context.Resource.Id, ResourceLifecycle.Running);
            return Task.CompletedTask;
        }

        public Task StopAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default)
        {
            StopToken = cancellationToken;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default)
        {
            DeleteToken = cancellationToken;
            return Task.CompletedTask;
        }
    }
}
