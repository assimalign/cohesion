using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public class ExternalResourceResolverTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - RemoteReference: Should inject command-line external endpoints into dependent environment")]
    public async Task StartAsync_CommandLineExternal_ShouldInjectDependencyEnvironment()
    {
        // Arrange
        var state = new InMemoryResourceStateManager();
        var controller = new DependencyEnvironmentController();
        var gateway = new TestGateway(state, [controller]);
        ExternalResourceDeclaration declaration = CreateDeclaration(["https"]);
        IApplicationBuilder builder = Application
            .CreateBuilder(
                ApplicationName.Parse("appa"),
                ["--external", "external-api=https=https://peer.example.test:7443"])
            .UseGateway(gateway);
        builder.RemoteReference(
            declaration,
            remote => remote.Endpoint("https", "https://code.example.test:443"));
        builder.AddResource(CreateConsumerManifest(declaration));
        IApplicationModel model = builder.Build().Model;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act
        await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);

        // Assert
        string url = ResourceEnvironment.Dependency("external-api", "https", "URL");
        string host = ResourceEnvironment.Dependency("external-api", "https", "HOST");
        string port = ResourceEnvironment.Dependency("external-api", "https", "PORT");
        controller.Environment[url].ShouldBe("https://peer.example.test:7443");
        controller.Environment[host].ShouldBe("peer.example.test");
        controller.Environment[port].ShouldBe("7443");

        await ((IApplicationGateway)gateway).StopAsync(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - RemoteReference: Should resolve a file export through gateway reconciliation")]
    public async Task StartAsync_FileRemoteReference_ShouldResolveExportEndToEnd()
    {
        // Arrange
        var state = new InMemoryResourceStateManager();
        var controller = new DependencyEnvironmentController();
        var gateway = new TestGateway(state, [controller]);
        ExternalResourceDeclaration declaration = CreateDeclaration(["https"]);
        string path = Path.GetTempFileName();

        try
        {
            IApplicationBuilder providerBuilder = Application
                .CreateBuilder(ApplicationName.Parse("peer"), [])
                .UseGateway(gateway);
            providerBuilder.AddResource(declaration.Manifest!);
            IApplicationModel providerModel = providerBuilder.Build().Model;
            ApplicationExportDocument.Create(
                providerModel,
                "1.0.0",
                new Dictionary<ResourceName, IReadOnlyList<ApplicationExportEndpoint>>
                {
                    ["external-api"] =
                    [
                        new ApplicationExportEndpoint(
                            "https",
                            "external-api.peer.svc:443",
                            "https://peer.example.test:7443"),
                    ],
                })
                .Save(path);

            IApplicationBuilder consumerBuilder = Application
                .CreateBuilder(ApplicationName.Parse("appa"), [])
                .UseGateway(gateway);
            IApplicationResourceDescriptor external = consumerBuilder.RemoteReference(
                declaration,
                remote => remote.File(path));
            consumerBuilder.AddResource(CreateConsumerManifest(declaration));
            IApplicationModel consumerModel = consumerBuilder.Build().Model;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // Act
            await ((IApplicationGateway)gateway).StartAsync(consumerModel, cancellation.Token);

            // Assert
            state.GetState(external.Resource.Id).ShouldBe(ResourceLifecycle.Running);
            state.GetObservedEndpoints(external.Resource.Id).ShouldContain(
                new ResourceEndpoint(
                    "https",
                    "https",
                    7443,
                    true,
                    "peer.example.test"));
            controller.Environment[
                ResourceEnvironment.Dependency("external-api", "https", "URL")]
                .ShouldBe("https://peer.example.test:7443");

            await ((IApplicationGateway)gateway).StopAsync(cancellation.Token);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - RemoteReference: Should skip an optional external when its export file is unavailable")]
    public async Task StartAsync_OptionalFileRemoteReferenceIsUnavailable_ShouldSkip()
    {
        // Arrange
        var state = new InMemoryResourceStateManager();
        var gateway = new TestGateway(
            state,
            [new RecordingController(new List<string>(), new List<string>())]);
        ExternalResourceDeclaration declaration = CreateDeclaration(["https"], optional: true);
        string path = Path.Combine(
            Path.GetTempPath(),
            $"cohesion-missing-{Guid.NewGuid():N}.json");
        IApplicationBuilder builder = Application
            .CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(gateway);
        IApplicationResourceDescriptor external = builder.RemoteReference(
            declaration,
            remote => remote.File(path));
        builder.AddResource(new TestResource("consumer"));
        IApplicationModel model = builder.Build().Model;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act
        await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);

        // Assert
        state.GetState(external.Resource.Id).ShouldBe(ResourceLifecycle.Skipped);

        await ((IApplicationGateway)gateway).StopAsync(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Realize: Should gather and reconcile a realized external in the current gateway")]
    public async Task StartAsync_RealizedExternal_ShouldUsePlatformArtifactAndController()
    {
        // Arrange
        var state = new InMemoryResourceStateManager();
        var reconciled = new List<string>();
        var gateway = new TestGateway(
            state,
            [new RecordingController(reconciled, new List<string>())],
            name: "local");
        ExternalResourceDeclaration declaration = CreateDeclaration(["https"]);
        IApplicationBuilder builder = Application
            .CreateBuilder(
                ApplicationName.Parse("appa"),
                ["--environment", AppEnvironment.Keys.Local, "--realize", "external-api"])
            .UseGateway(gateway);
        IApplicationResourceDescriptor external = builder.RemoteReference(
            declaration,
            remote => remote.Endpoint("https", "https://peer.example.test:7443"));
        builder.AddResource(CreateConsumerManifest(declaration));
        IApplicationModel model = builder.Build().Model;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act
        await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);

        // Assert
        gateway.Gathered.ShouldContain("external-api");
        reconciled.ShouldContain("external-api");
        state.GetState(external.Resource.Id).ShouldBe(ResourceLifecycle.Running);

        await ((IApplicationGateway)gateway).StopAsync(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - StartAsync: Should resolve externals without gathering or platform realization")]
    public async Task StartAsync_ResolvedExternal_ShouldPublishEndpointsWithoutGatheringArtifact()
    {
        // Arrange
        var state = new InMemoryResourceStateManager();
        var reconciled = new List<string>();
        var controlPlaneClient = new RecordingControlPlaneClient();
        var options = new ApplicationGatewayOptions
        {
            ControlPlaneClient = controlPlaneClient,
        };
        var gateway = new TestGateway(
            state,
            [new RecordingController(reconciled, new List<string>())],
            options: options);
        var resolver = new RecordingExternalResourceResolver(
            new ExternalResourceResolution(
                true,
                [new ResourceEndpoint("https", "https", 443, true, "peer.example.test")],
                detail: "Resolved by test resolver."));
        (IApplicationModel model, IApplicationResourceDescriptor external, _) = BuildModel(
            gateway,
            CreateDeclaration(["https"]),
            resolver);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act
        await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);

        // Assert
        state.GetState(external.Resource.Id).ShouldBe(ResourceLifecycle.Running);
        state.GetObservedEndpoints(external.Resource.Id).ShouldBe(
            [new ResourceEndpoint("https", "https", 443, true, "peer.example.test")]);
        gateway.Gathered.ShouldBe(["consumer"]);
        reconciled.ShouldBe(["consumer"]);
        resolver.Context.ShouldNotBeNull();
        resolver.Context!.ControlPlaneClient.ShouldBeSameAs(controlPlaneClient);

        await ((IApplicationGateway)gateway).StopAsync(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - StartAsync: Should skip an optional unresolved external and continue")]
    public async Task StartAsync_OptionalExternalIsUnresolved_ShouldSkipAndContinueIndependentResources()
    {
        // Arrange
        var state = new InMemoryResourceStateManager();
        var reconciled = new List<string>();
        var gateway = new TestGateway(
            state,
            [new RecordingController(reconciled, new List<string>())]);
        var resolver = new RecordingExternalResourceResolver(
            ExternalResourceResolution.Unresolved("Peer application is unavailable."));
        (IApplicationModel model, IApplicationResourceDescriptor external, _) = BuildModel(
            gateway,
            CreateDeclaration(["https"], optional: true),
            resolver,
            addDependency: false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act
        await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);

        // Assert
        state.GetState(external.Resource.Id).ShouldBe(ResourceLifecycle.Skipped);
        reconciled.ShouldBe(["consumer"]);

        await ((IApplicationGateway)gateway).StopAsync(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - StartAsync: Should budget an unresolved required external and block dependents")]
    public async Task StartAsync_RequiredExternalIsUnresolved_ShouldTimeOutAndBlockDependents()
    {
        // Arrange
        var state = new InMemoryResourceStateManager();
        var gateway = new TestGateway(
            state,
            [new RecordingController(new List<string>(), new List<string>())],
            readinessBudget: TimeSpan.FromMilliseconds(50));
        var resolver = new RecordingExternalResourceResolver(
            ExternalResourceResolution.Unresolved("Peer application is unavailable."));
        (IApplicationModel model, IApplicationResourceDescriptor external, IApplicationResourceDescriptor consumer) =
            BuildModel(gateway, CreateDeclaration(["https"]), resolver);
        ResourceStateChangedEventArgs? pending = null;
        ResourceStateChangedEventArgs? timedOut = null;
        state.StateChanged += (_, args) =>
        {
            if (args.Resource == external.Resource.Id && args.Current == ResourceLifecycle.Starting)
            {
                pending = args;
            }

            if (args.Resource == external.Resource.Id && args.Current == ResourceLifecycle.Failed)
            {
                timedOut = args;
            }
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act
        InvalidOperationException error = await Should.ThrowAsync<InvalidOperationException>(
            async () => await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token));

        // Assert
        error.Message.ShouldContain("Starting", Case.Sensitive);
        pending.ShouldNotBeNull();
        pending!.Detail.ShouldBe("Peer application is unavailable.");
        timedOut.ShouldNotBeNull();
        timedOut!.Detail!.ShouldContain("readiness budget", Case.Insensitive);
        state.GetState(consumer.Resource.Id).ShouldBe(ResourceLifecycle.Blocked);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - StartAsync: Should fail when a referenced external endpoint is absent")]
    public async Task StartAsync_ResolvedExternalMissesReferencedEndpoint_ShouldFailWithManifestDetail()
    {
        // Arrange
        var state = new InMemoryResourceStateManager();
        var gateway = new TestGateway(
            state,
            [new RecordingController(new List<string>(), new List<string>())]);
        ExternalResourceDeclaration declaration = CreateDeclaration(["grpc"]);
        const string observedHash = "sha256:observed";
        var resolver = new RecordingExternalResourceResolver(
            new ExternalResourceResolution(
                true,
                [new ResourceEndpoint("https", "https", 443, true, "peer.example.test")],
                observedHash,
                schemaVersion: 7));
        (IApplicationModel model, IApplicationResourceDescriptor external, IApplicationResourceDescriptor consumer) =
            BuildModel(gateway, declaration, resolver);
        ResourceStateChangedEventArgs? failure = null;
        state.StateChanged += (_, args) =>
        {
            if (args.Resource == external.Resource.Id && args.Current == ResourceLifecycle.Failed)
            {
                failure = args;
            }
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act
        InvalidOperationException error = await Should.ThrowAsync<InvalidOperationException>(
            async () => await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token));

        // Assert
        error.Message.ShouldContain("Failed", Case.Sensitive);
        failure.ShouldNotBeNull();
        failure!.Detail.ShouldNotBeNull();
        failure.Detail!.ShouldContain("grpc", Case.Sensitive);
        failure.Detail.ShouldContain(declaration.ManifestHash!, Case.Sensitive);
        failure.Detail.ShouldContain(observedHash, Case.Sensitive);
        failure.Detail.ShouldContain(
            $"schema {ApplicationExportDocument.CurrentSchemaVersion}",
            Case.Sensitive);
        failure.Detail.ShouldContain("schema 7", Case.Sensitive);
        state.GetState(consumer.Resource.Id).ShouldBe(ResourceLifecycle.Blocked);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - StartAsync: Should report manifest drift without blocking a compatible external")]
    public async Task StartAsync_ExternalManifestHashDiffers_ShouldReportDriftAndRemainRunning()
    {
        // Arrange
        var state = new InMemoryResourceStateManager();
        var gateway = new TestGateway(
            state,
            [new RecordingController(new List<string>(), new List<string>())]);
        ExternalResourceDeclaration declaration = CreateDeclaration(["https"]);
        const string observedHash = "sha256:observed";
        var resolver = new RecordingExternalResourceResolver(
            new ExternalResourceResolution(
                true,
                [new ResourceEndpoint("https", "https", 443, true, "peer.example.test")],
                observedHash,
                schemaVersion: 9));
        (IApplicationModel model, IApplicationResourceDescriptor external, _) = BuildModel(
            gateway,
            declaration,
            resolver);
        ResourceStateChangedEventArgs? running = null;
        state.StateChanged += (_, args) =>
        {
            if (args.Resource == external.Resource.Id && args.Current == ResourceLifecycle.Running)
            {
                running = args;
            }
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act
        await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);

        // Assert
        state.GetState(external.Resource.Id).ShouldBe(ResourceLifecycle.Running);
        running.ShouldNotBeNull();
        running!.Detail.ShouldNotBeNull();
        running.Detail!.ShouldContain(nameof(ManifestDrift), Case.Sensitive);
        running.Detail.ShouldContain(declaration.ManifestHash!, Case.Sensitive);
        running.Detail.ShouldContain(observedHash, Case.Sensitive);
        running.Detail.ShouldContain("schema 9", Case.Sensitive);

        await ((IApplicationGateway)gateway).StopAsync(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - ReconcileAsync: Should report new manifest drift while an external remains running")]
    public async Task ReconcileAsync_ExternalStartsDrifting_ShouldRaiseRunningObservation()
    {
        // Arrange
        var state = new InMemoryResourceStateManager();
        var gateway = new TestGateway(
            state,
            [new RecordingController(new List<string>(), new List<string>())]);
        ExternalResourceDeclaration declaration = CreateDeclaration(["https"]);
        var endpoints = new[]
        {
            new ResourceEndpoint("https", "https", 443, true, "peer.example.test"),
        };
        var resolver = new RecordingExternalResourceResolver(
            new ExternalResourceResolution(
                true,
                endpoints,
                declaration.ManifestHash,
                ApplicationExportDocument.CurrentSchemaVersion));
        (IApplicationModel model, IApplicationResourceDescriptor external, _) = BuildModel(
            gateway,
            declaration,
            resolver);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);
        ResourceStateChangedEventArgs? drift = null;
        state.StateChanged += (_, args) =>
        {
            if (args.Resource == external.Resource.Id &&
                args.Previous == ResourceLifecycle.Running &&
                args.Current == ResourceLifecycle.Running)
            {
                drift = args;
            }
        };
        resolver.Resolution = new ExternalResourceResolution(
            true,
            endpoints,
            declaration.ManifestHash,
            ApplicationExportDocument.CurrentSchemaVersion + 1);

        // Act
        await ((IApplicationGateway)gateway).ReconcileAsync(model, cancellation.Token);

        // Assert
        state.GetState(external.Resource.Id).ShouldBe(ResourceLifecycle.Running);
        drift.ShouldNotBeNull();
        drift!.Detail.ShouldNotBeNull();
        drift.Detail!.ShouldContain(nameof(ManifestDrift), Case.Sensitive);
        drift.Detail.ShouldContain(
            $"schema {ApplicationExportDocument.CurrentSchemaVersion + 1}",
            Case.Sensitive);

        await ((IApplicationGateway)gateway).StopAsync(cancellation.Token);
    }

    private static (
        IApplicationModel Model,
        IApplicationResourceDescriptor External,
        IApplicationResourceDescriptor Consumer) BuildModel(
        TestGateway gateway,
        ExternalResourceDeclaration declaration,
        IExternalResourceResolver resolver,
        bool addDependency = true)
    {
        IApplicationBuilder builder = Application
            .CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(gateway);
        IApplicationResourceDescriptor external = builder.RemoteReference(
            declaration,
            options => options.Bind(resolver));
        IApplicationResourceDescriptor consumer = builder.AddResource(new TestResource("consumer"));
        if (addDependency)
        {
            consumer.DependsOn(external);
        }

        return (builder.Build().Model, external, consumer);
    }

    private static ExternalResourceDeclaration CreateDeclaration(
        IReadOnlyList<string> referencedEndpoints,
        bool optional = false)
    {
        var endpoints = new ResourceManifestEndpoint[referencedEndpoints.Count];
        for (int index = 0; index < endpoints.Length; index++)
        {
            endpoints[index] = new ResourceManifestEndpoint
            {
                Name = referencedEndpoints[index],
                Scheme = referencedEndpoints[index] == "https" ? "https" : "http",
                Protocol = "tcp",
                ContainerPort = referencedEndpoints[index] == "https" ? 443 : 8080,
            };
        }

        return new ExternalResourceDeclaration(
            "external-api",
            "peer",
            referencedEndpoints,
            optional,
            new ResourceManifest
            {
                Name = "external-api",
                Application = "peer",
                Kind = "test",
                ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
                Artifact = new ResourceManifestArtifact
                {
                    Assembly = "peer.dll",
                    AppHost = "peer",
                },
                Endpoints = endpoints,
                ControlPlane = new ResourceManifestControlPlane
                {
                    Endpoint = endpoints[0].Name,
                    Path = "/cohesion/v1",
                },
                Lifecycle = new ResourceManifestLifecycle
                {
                    Workload = WorkloadKind.Deployment,
                },
            });
    }

    private static ResourceManifest CreateConsumerManifest(
        ExternalResourceDeclaration declaration) => new()
    {
        Name = "consumer",
        Application = "appa",
        Kind = "test",
        ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
        Artifact = new ResourceManifestArtifact
        {
            Assembly = "consumer.dll",
            AppHost = "consumer",
        },
        Endpoints =
        [
            new ResourceManifestEndpoint
            {
                Name = "control",
                Scheme = "http",
                Protocol = "tcp",
                ContainerPort = 8080,
            },
        ],
        ControlPlane = new ResourceManifestControlPlane
        {
            Endpoint = "control",
            Path = "/cohesion/v1",
        },
        References =
        [
            new ResourceManifestReference
            {
                Resource = declaration.Name,
                Application = declaration.Application,
                Endpoints = declaration.ReferencedEndpoints,
                Optional = declaration.Optional,
                Manifest = "peer.manifest.json",
            },
        ],
        Lifecycle = new ResourceManifestLifecycle
        {
            Workload = WorkloadKind.Deployment,
        },
    };

    private sealed class RecordingExternalResourceResolver : IExternalResourceResolver
    {
        public RecordingExternalResourceResolver(ExternalResourceResolution resolution)
        {
            Resolution = resolution;
        }

        public ExternalResourceResolutionContext? Context { get; private set; }

        public ExternalResourceResolution Resolution { get; set; }

        public ValueTask<ExternalResourceResolution> ResolveAsync(
            ExternalResourceResolutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Context = context;
            return ValueTask.FromResult(Resolution);
        }
    }

    private sealed class RecordingControlPlaneClient : IControlPlaneClient
    {
        public ValueTask<ApplicationExportDocument> GetApplicationAsync(
            Uri address,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The recording resolver does not query the client.");
    }

    private sealed class DependencyEnvironmentController : IApplicationResourceController
    {
        public IDictionary<string, string> Environment { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

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
            ObservedDependencyEnvironment.Apply(context.ObservedDependencies, Environment);
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
}
