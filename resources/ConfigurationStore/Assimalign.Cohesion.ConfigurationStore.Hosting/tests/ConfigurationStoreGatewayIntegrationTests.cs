using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway;
using Assimalign.Cohesion.ConfigurationStore;
using Assimalign.Cohesion.ConfigurationStore.ApplicationModel;
using Assimalign.Cohesion.Hosting.Resources;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Tests;

public sealed class ConfigurationStoreGatewayIntegrationTests
{
    [Theory(DisplayName = "Cohesion Test [ConfigurationStore.Hosting] - Gateway mount: loopback HTTP resolution is Local-only")]
    [InlineData(AppEnvironment.Keys.Local, true)]
    [InlineData(AppEnvironment.Keys.Development, false)]
    public async Task StartAsync_WithLoopbackConfigurationMount_ShouldEnforceLocalTransport(
        string environmentName,
        bool expectedResolved)
    {
        // Arrange
        string temporaryRoot = CreateTemporaryDirectory();
        string dataPath = Path.Combine(temporaryRoot, "data");
        Directory.CreateDirectory(dataPath);
        Uri endpoint = ConfigurationStoreTestHost.GetEndpoint();
        var state = new InMemoryResourceStateManager();
        var controller = new ConfigurationStoreGatewayController(endpoint, dataPath);
        var gateway = new ConfigurationStoreTestGateway(
            state,
            [controller],
            new ApplicationGatewayOptions
            {
                ExportDirectory = Path.Combine(temporaryRoot, "gateway"),
                ReadinessBudget = TimeSpan.FromSeconds(15),
                TrustKeyRepository = new EphemeralTrustKeyRepository(),
            });
        IApplicationGateway gatewayControl = gateway;

        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                ["--environment", environmentName])
            .UseGateway(gateway);
        IApplicationResourceDescriptor configuration = builder.AddConfigurationStore(
            CreateConfigurationManifest());
        IApplicationResourceDescriptor consumer = builder.AddResource(CreateConsumerManifest());
        consumer.DependsOn(configuration);
        IApplicationModel model = builder.Build().Model;

        try
        {
            // Act
            await gatewayControl.StartAsync(model, CancellationToken.None);

            // Assert
            ResourceInputs configurationInputs = controller.ConfigurationInputs.ShouldNotBeNull();
            configurationInputs.BootstrapCredential.IsEmpty.ShouldBeFalse();
            configurationInputs.ApplicationTrustKey.IsEmpty.ShouldBeFalse();

            ResourceInputs consumerInputs = controller.ConsumerInputs.ShouldNotBeNull();
            ResourceMountInput mount = consumerInputs.Mounts["features"];
            mount.IsResolved.ShouldBe(expectedResolved);
            if (expectedResolved)
            {
                mount.UnresolvedReason.ShouldBeNull();
                Encoding.UTF8.GetString(mount.Content.Span)
                    .ShouldBe("{\"alpha\":\"on\",\"zeta\":null}");
            }
            else
            {
                mount.Content.IsEmpty.ShouldBeTrue();
                mount.UnresolvedReason.ShouldNotBeNull()
                    .ShouldContain("refuses to send a bearer credential", Case.Sensitive);
                mount.UnresolvedReason.ShouldContain("loopback HTTP in Local", Case.Sensitive);
            }
        }
        finally
        {
            try
            {
                await gatewayControl.StopAsync(CancellationToken.None);
            }
            finally
            {
                try
                {
                    await controller.DisposeAsync();
                }
                finally
                {
                    if (Directory.Exists(temporaryRoot))
                    {
                        Directory.Delete(temporaryRoot, recursive: true);
                    }
                }
            }
        }
    }

    private static ResourceManifest CreateConfigurationManifest()
    {
        return new ResourceManifest
        {
            Name = "configuration",
            Application = "appa",
            Kind = "ConfigurationStore",
            ApplicationModel = "Assimalign.Cohesion.ConfigurationStore.ApplicationModel",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "Example.AppA.ConfigurationStore",
                Composable = true,
            },
            Endpoints =
            [
                new ResourceManifestEndpoint
                {
                    Name = "api",
                    Scheme = "http",
                    Protocol = "tcp",
                    ContainerPort = 8080,
                },
            ],
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = "api",
                Path = "/cohesion/v1",
            },
            Mounts =
            [
                new ResourceManifestMount
                {
                    Name = "data",
                    Kind = ResourceMountKind.Volume,
                    ContainerPath = "/data",
                    Size = "1Gi",
                },
            ],
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = WorkloadKind.StatefulSet,
                Replicas = 1,
                MaxReplicas = 1,
                StopGraceSeconds = 5,
            },
        };
    }

    private static ResourceManifest CreateConsumerManifest()
    {
        return new ResourceManifest
        {
            Name = "consumer",
            Application = "appa",
            Kind = "Web",
            ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "Example.AppA.Consumer",
                AppHost = "consumer",
            },
            Endpoints =
            [
                new ResourceManifestEndpoint
                {
                    Name = "api",
                    Scheme = "http",
                    Protocol = "tcp",
                    ContainerPort = 8080,
                },
            ],
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = "api",
                Path = "/cohesion/v1",
            },
            Mounts =
            [
                new ResourceManifestMount
                {
                    Name = "features",
                    Kind = ResourceMountKind.Configuration,
                    Source = "configuration:features",
                    ContainerPath = "/inputs/features",
                },
            ],
            References =
            [
                new ResourceManifestReference
                {
                    Resource = "configuration",
                    Application = "appa",
                    Endpoints = ["api"],
                    Manifest = "Assimalign.Cohesion.ConfigurationStore.ApplicationModel",
                },
            ],
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = WorkloadKind.Deployment,
                Replicas = 1,
                RestartPolicy = "Never",
            },
        };
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "cohesion-configuration-store-gateway-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ConfigurationStoreGatewayController :
        IApplicationResourceController,
        IAsyncDisposable
    {
        private readonly string _dataPath;
        private readonly Uri _endpoint;
        private IConfigurationStoreApplication? _application;
        private ResourceContext? _resourceContext;

        internal ConfigurationStoreGatewayController(Uri endpoint, string dataPath)
        {
            _endpoint = endpoint;
            _dataPath = dataPath;
        }

        internal ResourceInputs? ConfigurationInputs { get; private set; }

        internal ResourceInputs? ConsumerInputs { get; private set; }

        public bool CanRealize(ResourcePlan plan, out string? reason)
        {
            reason = null;
            return true;
        }

        public async Task ReconcileAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string resourceName = context.Resource.Name.ToString();
            if (string.Equals(resourceName, "configuration", StringComparison.Ordinal))
            {
                ConfigurationInputs = context.Inputs;
                await StartConfigurationStoreAsync(context.Inputs, cancellationToken)
                    .ConfigureAwait(false);
                context.State.SetState(
                    context.Resource.Id,
                    ResourceLifecycle.Running,
                    observedEndpoints:
                    [
                        new ResourceEndpoint(
                            "api",
                            "http",
                            _endpoint.Port,
                            Host: "127.0.0.1"),
                    ]);
                return;
            }

            if (string.Equals(resourceName, "consumer", StringComparison.Ordinal))
            {
                ConsumerInputs = context.Inputs;
                context.State.SetState(context.Resource.Id, ResourceLifecycle.Running);
                return;
            }

            throw new InvalidOperationException(
                $"The test controller cannot realize resource '{resourceName}'.");
        }

        public async Task StopAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(
                    context.Resource.Name.ToString(),
                    "configuration",
                    StringComparison.Ordinal))
            {
                await StopConfigurationStoreAsync(cancellationToken).ConfigureAwait(false);
            }

            context.State.SetState(context.Resource.Id, ResourceLifecycle.Stopped);
        }

        public Task DeleteAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default)
        {
            return StopAsync(context, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await StopConfigurationStoreAsync(CancellationToken.None).ConfigureAwait(false);
        }

        private async Task StartConfigurationStoreAsync(
            ResourceInputs inputs,
            CancellationToken cancellationToken)
        {
            if (_application is not null)
            {
                return;
            }

            if (inputs.BootstrapCredential.IsEmpty || inputs.ApplicationTrustKey.IsEmpty)
            {
                throw new InvalidOperationException(
                    "The gateway did not supply ConfigurationStore bootstrap trust inputs.");
            }

            string credential = Encoding.ASCII.GetString(inputs.BootstrapCredential.Span);
            ResourceContext resourceContext = ConfigurationStoreTestHost.CreateContext(
                _endpoint,
                _dataPath,
                credential,
                inputs.ApplicationTrustKey,
                gatewayName: "local");

            IConfigurationStoreApplication application;
            using (ResourceRuntime.CreateScope(resourceContext))
            {
                IConfigurationStoreApplicationBuilder builder =
                    ConfigurationStoreTestHost.CreateBuilder();
                builder.AddNamespace("features", ns => ns
                    .Set("zeta", null)
                    .Set("alpha", "on"));
                application = builder.Build();
                try
                {
                    await application.StartAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await application.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            _resourceContext = resourceContext;
            _application = application;
        }

        private async Task StopConfigurationStoreAsync(CancellationToken cancellationToken)
        {
            IConfigurationStoreApplication? application = _application;
            ResourceContext? resourceContext = _resourceContext;
            _application = null;
            _resourceContext = null;
            if (application is null)
            {
                return;
            }

            using IDisposable? scope = resourceContext is null
                ? null
                : ResourceRuntime.CreateScope(resourceContext);
            try
            {
                await application.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await application.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class ConfigurationStoreTestGateway : ApplicationGateway
    {
        private readonly IReadOnlyList<IApplicationResourceController> _controllers;
        private readonly IApplicationResourceStateManager _state;

        internal ConfigurationStoreTestGateway(
            IApplicationResourceStateManager state,
            IReadOnlyList<IApplicationResourceController> controllers,
            ApplicationGatewayOptions options)
            : base(options)
        {
            _state = state;
            _controllers = controllers;
        }

        public override ResourceName Name => (ResourceName)"local";

        protected override IReadOnlyList<IApplicationResourceController> Controllers => _controllers;

        protected override IApplicationResourceStateManager State => _state;

        protected override Task<IResourceArtifact> GatherAsync(
            IApplicationResource resource,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IResourceArtifact>(new TestArtifact(resource.Id));
        }

        protected override Task PublishApplicationExportAsync(
            ApplicationExportDocument document,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        protected override Task RemoveApplicationExportAsync(
            ApplicationName application,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class TestArtifact : IResourceArtifact
    {
        internal TestArtifact(ResourceId resource)
        {
            Resource = resource;
        }

        public ResourceId Resource { get; }
    }

    private sealed class EphemeralTrustKeyRepository : IGatewayTrustKeyRepository
    {
        public Task<ECDsa> LoadOrCreateAsync(
            ApplicationName application,
            ResourceName gateway,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        }

        public Task<ECDsa> RotateAsync(
            ApplicationName application,
            ResourceName gateway,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        }
    }
}
