using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

[Collection(LocalGatewayConsoleCollection.Name)]
public class ApplicationSetGatewayTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Application set: Should resolve two model documents into one ordered gateway start")]
    public async Task ApplicationSet_FileModelDocuments_ShouldStartOneOrderedGatewayBatch()
    {
        // Arrange
        string root = Path.Combine(
            Path.GetTempPath(),
            $"cohesion-model-set-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string appBPath = Path.Combine(root, "appb-export.json");
        string appAPath = Path.Combine(root, "appa-export.json");
        var state = new InMemoryResourceStateManager();
        var controller = new MultiModelRecordingController();
        var gateway = new MultiModelTestGateway(state, controller);
        ApplicationExportDocument.Create(BuildModel("appb", gateway), "1.0.0").Save(appBPath);
        ApplicationExportDocument.Create(BuildModel("appa", gateway), "1.0.0").Save(appAPath);
        IApplicationSet set = Application.CreateSet(
                gateway,
                ["--gateway=multi-test", "--environment=Development"])
            .AddApplication(new ApplicationDeclaration(
                ApplicationName.Parse("appb"),
                ApplicationModelResolvers.File(appBPath)))
            .AddApplication(new ApplicationDeclaration(
                ApplicationName.Parse("appa"),
                ApplicationModelResolvers.File(appAPath)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            // Act
            Task run = set.RunAsync(cancellation.Token);
            await controller.FirstBatch.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            gateway.ObserverStarts.ShouldBe(1);
            gateway.ObserverModels.ShouldBe(["appb", "appa"]);
            controller.Reconciled.ShouldBe(
            [
                "appb/shared",
                "appb/tail",
                "appa/shared",
                "appa/tail",
            ]);
            gateway.ObserverStops.ShouldBe(1);
        }
        finally
        {
            cancellation.Cancel();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Multi-model session: Should preserve member order and isolate equal resource names")]
    public async Task MultiModelSession_ShouldUseOneObserverAndReverseLifecycleOrder()
    {
        // Arrange
        var state = new InMemoryResourceStateManager();
        var controller = new MultiModelRecordingController();
        var gateway = new MultiModelTestGateway(state, controller);
        IApplicationModel appB = BuildModel("appb", gateway);
        IApplicationModel appA = BuildModel("appa", gateway);
        IReadOnlyList<IApplicationModel> models = new[] { appB, appA };
        IMultiModelApplicationGateway control = gateway;

        appB.Resources[0].Id.ShouldBe(appA.Resources[0].Id);

        // Act
        control.Validate(models);
        await control.StartAsync(models);

        // Assert
        gateway.ObserverStarts.ShouldBe(1);
        gateway.ObserverModels.ShouldBe(new[] { "appb", "appa" });
        controller.Reconciled.ShouldBe(
        [
            "appb/shared",
            "appb/tail",
            "appa/shared",
            "appa/tail",
        ]);

        controller.StateByApplication["appb"]
            .GetObservedEndpoints(appB.Resources[0].Id)[0]
            .Port.ShouldBe(5101);
        controller.StateByApplication["appa"]
            .GetObservedEndpoints(appA.Resources[0].Id)[0]
            .Port.ShouldBe(5102);

        await control.ReconcileAsync(models);

        gateway.ObserverStarts.ShouldBe(1);
        controller.Reconciled.ShouldBe(
        [
            "appb/shared",
            "appb/tail",
            "appa/shared",
            "appa/tail",
            "appb/shared",
            "appb/tail",
            "appa/shared",
            "appa/tail",
        ]);

        await control.StopAsync();

        controller.Stopped.ShouldBe(
        [
            "appa/tail",
            "appa/shared",
            "appb/tail",
            "appb/shared",
        ]);
        gateway.ObserverStops.ShouldBe(1);

        await control.UninstallAsync(models);

        controller.Deleted.ShouldBe(
        [
            "appa/tail",
            "appa/shared",
            "appb/tail",
            "appb/shared",
        ]);
        gateway.ObserverStarts.ShouldBe(2);
        gateway.ObserverStops.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Multi-model validation: Should reject a missing manifest before target contact")]
    public void MultiModelValidation_ManifestCountDiffers_ShouldFailPreflight()
    {
        // Arrange
        var gateway = new MultiModelTestGateway(
            new InMemoryResourceStateManager(),
            new MultiModelRecordingController());
        IApplicationModel valid = BuildModel("appa", gateway);
        var malformed = new MissingManifestApplicationModel(valid);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => ((IMultiModelApplicationGateway)gateway).Validate([malformed]));

        // Assert
        error.Message.ShouldContain("descriptor, manifest, and plan counts differ", Case.Sensitive);
        gateway.ObserverStarts.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local multi-model session: Should supervise equal resource names independently")]
    public async Task LocalMultiModelSession_ShouldSuperviseEqualResourceNamesIndependently()
    {
        // Arrange
        string root = Path.Combine(
            Path.GetTempPath(),
            $"cohesion-local-set-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string appABound = Path.Combine(root, "appa-bound.txt");
        string appBBound = Path.Combine(root, "appb-bound.txt");
        var options = new LocalGatewayOptions
        {
            BaseDirectory = AppContext.BaseDirectory,
            StateDirectory = Path.Combine(root, ".cohesion"),
            ProbeInterval = TimeSpan.FromMilliseconds(50),
            ProbeTimeout = TimeSpan.FromSeconds(2),
            ReadinessBudget = TimeSpan.FromSeconds(10),
            StopGrace = TimeSpan.FromMilliseconds(500),
        };
        var gateway = new LocalGateway(options);
        IApplicationModel appA = BuildLocalModel("local-appa", gateway, appABound);
        IApplicationModel appB = BuildLocalModel("local-appb", gateway, appBBound);
        IReadOnlyList<IApplicationModel> models = new[] { appA, appB };
        IMultiModelApplicationGateway control = gateway;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        appA.Resources[0].Id.ShouldBe(appB.Resources[0].Id);

        try
        {
            // Act
            await control.StartAsync(models, cancellation.Token);

            // Assert
            File.Exists(appABound).ShouldBeTrue();
            File.Exists(appBBound).ShouldBeTrue();
            File.Exists(ProcessPath(root, appA.Name, "shared")).ShouldBeTrue();
            File.Exists(ProcessPath(root, appB.Name, "shared")).ShouldBeTrue();

            await control.StopAsync(cancellation.Token);
        }
        finally
        {
            try
            {
                await control.StopAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }
    }

    private static IApplicationModel BuildModel(
        string name,
        IApplicationGateway gateway)
    {
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse(name),
                [])
            .UseGateway(gateway);
        IApplicationResourceDescriptor shared = builder.AddResource(new TestResource("shared"));
        IApplicationResourceDescriptor tail = builder.AddResource(new TestResource("tail"));
        tail.DependsOn(shared);
        return builder.Build().Model;
    }

    private static IApplicationModel BuildLocalModel(
        string name,
        IApplicationGateway gateway,
        string boundPath)
    {
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse(name),
                [])
            .UseGateway(gateway);
        builder.AddExecutable(
            "shared",
            TestHostPath,
            options => options
                .UseReadyMarker("cohesion-resource: ready")
                .AddEndpoint(new ResourceEndpoint("http", "http", 0))
                .AddEnvironment("TEST_BOUND_PATH", boundPath));
        return builder.Build().Model;
    }

    private static string TestHostPath => Path.Combine(
        AppContext.BaseDirectory,
        "Assimalign.Cohesion.ApplicationModel.Gateway.TestHost"
        + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));

    private static string ProcessPath(
        string root,
        ApplicationName application,
        ResourceName resource) =>
        Path.Combine(
            root,
            ".cohesion",
            application.ToString(),
            ".state",
            resource.ToString(),
            "pid");

    private sealed class MultiModelTestGateway : ApplicationGateway
    {
        private readonly IApplicationResourceStateManager _state;
        private readonly IReadOnlyList<IApplicationResourceController> _controllers;

        public MultiModelTestGateway(
            IApplicationResourceStateManager state,
            IApplicationResourceController controller)
        {
            _state = state;
            _controllers = new[] { controller };
        }

        public override ResourceName Name => "multi-test";

        public int ObserverStarts { get; private set; }

        public int ObserverStops { get; private set; }

        public List<string> ObserverModels { get; } = new();

        protected override IReadOnlyList<IApplicationResourceController> Controllers => _controllers;

        protected override IApplicationResourceStateManager State => _state;

        protected override Task<IResourceArtifact> GatherAsync(
            IApplicationResource resource,
            CancellationToken cancellationToken) =>
            Task.FromResult<IResourceArtifact>(new TestArtifact(resource.Id));

        protected override Task StartObserverAsync(
            IReadOnlyList<IApplicationModel> models,
            CancellationToken cancellationToken)
        {
            ObserverStarts++;
            foreach (IApplicationModel model in models)
            {
                ObserverModels.Add(model.Name.ToString());
                _ = GetApplicationState(model);
            }

            return Task.CompletedTask;
        }

        protected override Task StopObserverAsync(CancellationToken cancellationToken)
        {
            ObserverStops++;
            return Task.CompletedTask;
        }

        protected override Task PublishApplicationExportAsync(
            ApplicationExportDocument document,
            CancellationToken cancellationToken) => Task.CompletedTask;

        protected override Task RemoveApplicationExportAsync(
            ApplicationName application,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MultiModelRecordingController : IApplicationResourceController
    {
        private readonly TaskCompletionSource _firstBatch = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Reconciled { get; } = new();

        public Task FirstBatch => _firstBatch.Task;

        public List<string> Stopped { get; } = new();

        public List<string> Deleted { get; } = new();

        public Dictionary<string, IApplicationResourceStateManager> StateByApplication { get; } =
            new(StringComparer.Ordinal);

        public bool CanRealize(ResourcePlan plan, out string? reason)
        {
            reason = null;
            return true;
        }

        public Task ReconcileAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default)
        {
            string application = context.Model.Name.ToString();
            Reconciled.Add($"{application}/{context.Resource.Name}");
            if (Reconciled.Count == 4)
            {
                _firstBatch.TrySetResult();
            }

            StateByApplication[application] = context.State;

            int port = application == "appb" ? 5101 : 5102;
            context.State.SetState(
                context.Resource.Id,
                ResourceLifecycle.Running,
                observedEndpoints:
                [
                    new ResourceEndpoint("http", "http", port, Host: "localhost"),
                ]);
            return Task.CompletedTask;
        }

        public Task StopAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default)
        {
            Stopped.Add($"{context.Model.Name}/{context.Resource.Name}");
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default)
        {
            Deleted.Add($"{context.Model.Name}/{context.Resource.Name}");
            return Task.CompletedTask;
        }
    }

    private sealed class TestArtifact : IResourceArtifact
    {
        public TestArtifact(ResourceId resource)
        {
            Resource = resource;
        }

        public ResourceId Resource { get; }
    }

    private sealed class MissingManifestApplicationModel : IApplicationModel
    {
        private readonly IApplicationModel _inner;

        public MissingManifestApplicationModel(IApplicationModel inner)
        {
            _inner = inner;
        }

        public ApplicationName Name => _inner.Name;

        public IApplicationEnvironment Environment => _inner.Environment;

        public GatewayRunMode RunMode => _inner.RunMode;

        public ResourceName GatewayIdentity => _inner.GatewayIdentity;

        public string Owner => _inner.Owner;

        public bool Adopt => _inner.Adopt;

        public bool RestartOrphans => _inner.RestartOrphans;

        public IReadOnlyList<IApplicationResourceDescriptor> Descriptors => _inner.Descriptors;

        public IReadOnlyList<IApplicationResource> Resources => _inner.Resources;

        public IReadOnlyList<ResourceManifest> Manifests => Array.Empty<ResourceManifest>();

        public IReadOnlyList<ResourcePlan> Plans => _inner.Plans;
    }
}
