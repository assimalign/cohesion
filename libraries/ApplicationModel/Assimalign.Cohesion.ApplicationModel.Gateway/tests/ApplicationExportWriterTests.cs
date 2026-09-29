using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public class ApplicationExportWriterTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - StartAsync: Should publish a source-generated application export after reconciliation")]
    public async Task StartAsync_RealizedResource_ShouldWriteExportJson()
    {
        // Arrange
        string root = Path.Combine(
            Path.GetTempPath(),
            "cohesion-export-tests",
            Guid.NewGuid().ToString("N"));
        string exportRoot = Path.Combine(root, ".cohesion");
        var options = new ApplicationGatewayOptions
        {
            ApplicationVersion = "2.3.4",
            ExportDirectory = exportRoot,
        };
        var state = new InMemoryResourceStateManager();
        var gateway = new ExportTestGateway(options, state, new ExportingController());
        IApplicationBuilder builder = Application
            .CreateBuilder(
                ApplicationName.Parse("appa"),
                ["--environment", "Development"])
            .UseGateway(gateway);
        builder.AddResource(CreateManifest());
        IApplicationModel model = builder.Build().Model;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            // Act
            await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);

            // Assert
            string path = Path.Combine(exportRoot, "appa", "export.json");
            File.Exists(path).ShouldBeTrue();
            ApplicationExportDocument export = await ApplicationExportDocument.LoadAsync(
                path,
                cancellation.Token);
            export.Application.ShouldBe("appa");
            export.Environment.ShouldBe("Development");
            export.Version.ShouldBe("2.3.4");
            export.Resources.Count.ShouldBe(1);
            export.Resources[0].Name.ShouldBe("api");
            export.Resources[0].Endpoints.Count.ShouldBe(1);
            export.Resources[0].Endpoints[0].Internal.ShouldBe("api.appa.svc:8080");
            export.Resources[0].Endpoints[0].Public.ShouldBe("https://api.example.test:443");
            export.Model.Resources.Count.ShouldBe(1);
            export.ToModel().Name.ShouldBe(ApplicationName.Parse("appa"));

            await ((IApplicationGateway)gateway).StopAsync(cancellation.Token);
            File.Exists(path).ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - StartAsync: Should withdraw earlier exports when later publication fails")]
    public async Task StartAsync_SecondExportFails_ShouldWithdrawPublishedModels()
    {
        // Arrange
        var gateway = new FaultingExportGateway(failPublishFor: "appb");
        IReadOnlyList<IApplicationModel> models =
        [
            BuildModel("appa", gateway),
            BuildModel("appb", gateway),
        ];

        // Act
        await Should.ThrowAsync<IOException>(
            async () => await ((IMultiModelApplicationGateway)gateway).StartAsync(models));

        // Assert
        gateway.Published.ShouldBe(["appa"]);
        gateway.Removed.ShouldBe(["appa", "appb"]);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - StopAsync: Should attempt every export removal and retain cleanup context for retry")]
    public async Task StopAsync_ExportRemovalFailsOnce_ShouldRetryActiveModels()
    {
        // Arrange
        var gateway = new FaultingExportGateway(failRemovalOnceFor: "appa");
        IReadOnlyList<IApplicationModel> models =
        [
            BuildModel("appa", gateway),
            BuildModel("appb", gateway),
        ];
        IMultiModelApplicationGateway control = gateway;
        await control.StartAsync(models);

        // Act
        InvalidOperationException error = await Should.ThrowAsync<InvalidOperationException>(
            async () => await control.StopAsync());
        await control.StopAsync();

        // Assert
        error.InnerException.ShouldBeOfType<IOException>();
        gateway.Removed.ShouldBe(["appa", "appb", "appa", "appb"]);
    }

    private static IApplicationModel BuildModel(
        string application,
        IApplicationGateway gateway)
    {
        IApplicationBuilder builder = Application
            .CreateBuilder(ApplicationName.Parse(application), [])
            .UseGateway(gateway);
        builder.AddResource(CreateManifest() with
        {
            Application = application,
        });
        return builder.Build().Model;
    }

    private static ResourceManifest CreateManifest() => new()
    {
        Name = "api",
        Application = "appa",
        Kind = "test",
        ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
        Artifact = new ResourceManifestArtifact
        {
            Assembly = "api.dll",
            AppHost = "api",
        },
        Endpoints =
        [
            new ResourceManifestEndpoint
            {
                Name = "https",
                Scheme = "https",
                Protocol = "tcp",
                ContainerPort = 8080,
                Public = true,
            },
        ],
        ControlPlane = new ResourceManifestControlPlane
        {
            Endpoint = "https",
            Path = "/cohesion/v1",
        },
        Lifecycle = new ResourceManifestLifecycle
        {
            Workload = WorkloadKind.Deployment,
        },
    };

    private sealed class ExportTestGateway : ApplicationGateway
    {
        private readonly IApplicationResourceStateManager _state;
        private readonly IReadOnlyList<IApplicationResourceController> _controllers;

        public ExportTestGateway(
            ApplicationGatewayOptions options,
            IApplicationResourceStateManager state,
            IApplicationResourceController controller)
            : base(options)
        {
            _state = state;
            _controllers = [controller];
        }

        public override ResourceName Name => "export-test";

        protected override IReadOnlyList<IApplicationResourceController> Controllers => _controllers;

        protected override IApplicationResourceStateManager State => _state;

        protected override Task<IResourceArtifact> GatherAsync(
            IApplicationResource resource,
            CancellationToken cancellationToken) =>
            Task.FromResult<IResourceArtifact>(new ExportArtifact(resource.Id));
    }

    private sealed class ExportingController : IApplicationResourceController
    {
        public bool CanRealize(ResourcePlan plan, out string? reason)
        {
            reason = null;
            return true;
        }

        public Task ReconcileAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default)
        {
            context.State.SetState(
                context.Resource.Id,
                ResourceLifecycle.Running,
                observedEndpoints:
                [
                    new ResourceEndpoint(
                        "https",
                        "https",
                        8080,
                        Host: "api.appa.svc"),
                    new ResourceEndpoint(
                        "https",
                        "https",
                        443,
                        IsPublic: true,
                        Host: "api.example.test"),
                ]);
            return Task.CompletedTask;
        }

        public Task StopAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default)
        {
            context.State.SetState(context.Resource.Id, ResourceLifecycle.Stopped);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default) => StopAsync(context, cancellationToken);
    }

    private sealed class ExportArtifact : IResourceArtifact
    {
        public ExportArtifact(ResourceId resource)
        {
            Resource = resource;
        }

        public ResourceId Resource { get; }
    }

    private sealed class FaultingExportGateway : ApplicationGateway
    {
        private readonly string? _failPublishFor;
        private readonly string? _failRemovalOnceFor;
        private readonly InMemoryResourceStateManager _state = new();
        private readonly IReadOnlyList<IApplicationResourceController> _controllers =
            [new ExportingController()];
        private bool _removalFailed;

        public FaultingExportGateway(
            string? failPublishFor = null,
            string? failRemovalOnceFor = null)
        {
            _failPublishFor = failPublishFor;
            _failRemovalOnceFor = failRemovalOnceFor;
        }

        public override ResourceName Name => "export-test";

        public List<string> Published { get; } = new();

        public List<string> Removed { get; } = new();

        protected override IReadOnlyList<IApplicationResourceController> Controllers => _controllers;

        protected override IApplicationResourceStateManager State => _state;

        protected override Task<IResourceArtifact> GatherAsync(
            IApplicationResource resource,
            CancellationToken cancellationToken) =>
            Task.FromResult<IResourceArtifact>(new ExportArtifact(resource.Id));

        protected override Task PublishApplicationExportAsync(
            ApplicationExportDocument document,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(document.Application, _failPublishFor, StringComparison.Ordinal))
            {
                throw new IOException($"Could not publish '{document.Application}'.");
            }

            Published.Add(document.Application);
            return Task.CompletedTask;
        }

        protected override Task RemoveApplicationExportAsync(
            ApplicationName application,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = application.ToString();
            Removed.Add(name);
            if (!_removalFailed &&
                string.Equals(name, _failRemovalOnceFor, StringComparison.Ordinal))
            {
                _removalFailed = true;
                throw new IOException($"Could not remove '{name}'.");
            }

            return Task.CompletedTask;
        }
    }
}
