using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;
using Assimalign.Cohesion.SecretStore;

namespace Assimalign.Cohesion.SecretStore.Hosting.Tests;

public sealed class GatewayStoreResolutionEndToEndTests
{
    [Fact(DisplayName = "Cohesion Test [SecretStore.Hosting] - Gateway mount resolution: real host resolves secret and certificate mounts with controller credentials")]
    public async Task StartAsync_WithRealSecretStoreHost_ShouldResolveSecretAndCertificateMounts()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        Uri storeEndpoint = SecretStoreTestHost.GetEndpoint();
        var controller = new LoopbackSecretStoreController(
            storeEndpoint,
            Path.Combine(directory.Path, "store"));
        var state = new InMemoryResourceStateManager();
        var options = new ApplicationGatewayOptions
        {
            ExportDirectory = Path.Combine(directory.Path, "gateway"),
            ReadinessBudget = TimeSpan.FromSeconds(30),
        };
        var gateway = new LoopbackSecretStoreGateway(state, controller, options);
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(gateway);
        IApplicationResourceDescriptor store = builder.AddResource(CreateManifest(
            "secrets",
            "SecretStore"));
        IApplicationResourceDescriptor dependent = builder.AddResource(CreateManifest(
            "api",
            "Web",
            mounts:
            [
                CreateMount("api-key", "secrets:app/api-key"),
                CreateMount("tls", "secrets:certs/appa-api"),
            ],
            certificateMount: "tls",
            referenceResources: ["secrets"]));
        dependent.DependsOn(store);
        IApplicationModel model = builder.Build().Model;
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            // Act
            await ((IApplicationGateway)gateway).StartAsync(model, cancellationTokenSource.Token);

            // Assert
            ResourceInputs storeInputs = controller.StoreInputs.ShouldNotBeNull();
            ResourceInputs dependentInputs = controller.DependentInputs.ShouldNotBeNull();
            storeInputs.BootstrapCredential.IsEmpty.ShouldBeFalse();
            storeInputs.ApplicationTrustKey.IsEmpty.ShouldBeFalse();
            string bootstrapToken = Encoding.ASCII.GetString(storeInputs.BootstrapCredential.Span);
            JsonWebToken.Parse(bootstrapToken).Audiences.ShouldBe(["secrets"]);
            dependentInputs.Mounts["api-key"].IsResolved.ShouldBeTrue();
            Encoding.UTF8.GetString(dependentInputs.Mounts["api-key"].Content.Span)
                .ShouldBe("gateway-secret");
            dependentInputs.Mounts["tls"].IsResolved.ShouldBeTrue();
            string certificate = Encoding.UTF8.GetString(
                dependentInputs.Mounts["tls"].Content.Span);
            certificate.ShouldContain("BEGIN CERTIFICATE", Case.Sensitive);
            certificate.ShouldContain("BEGIN PRIVATE KEY", Case.Sensitive);
        }
        finally
        {
            try
            {
                await ((IApplicationGateway)gateway).StopAsync(CancellationToken.None);
            }
            finally
            {
                await controller.DisposeAsync();
            }
        }
    }

    private static ResourceManifestMount CreateMount(string name, string source) => new()
    {
        Name = name,
        ContainerPath = "/inputs/" + name,
        Kind = ResourceMountKind.Secret,
        Source = source,
    };

    private static ResourceManifest CreateManifest(
        string name,
        string kind,
        IReadOnlyList<ResourceManifestMount>? mounts = null,
        string? certificateMount = null,
        IReadOnlyList<string>? referenceResources = null) => new()
        {
            Name = name,
            Application = "appa",
            Kind = kind,
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
                    Name = "api",
                    Scheme = "http",
                    Protocol = "tcp",
                    ContainerPort = 8080,
                    Certificate = certificateMount,
                },
            ],
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = "api",
                Path = "/cohesion/v1",
            },
            Mounts = mounts ?? Array.Empty<ResourceManifestMount>(),
            References = CreateReferences(referenceResources),
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = WorkloadKind.Deployment,
                Replicas = 1,
                RestartPolicy = "Never",
            },
        };

    private static IReadOnlyList<ResourceManifestReference> CreateReferences(
        IReadOnlyList<string>? resources)
    {
        if (resources is null)
        {
            return Array.Empty<ResourceManifestReference>();
        }

        var references = new ResourceManifestReference[resources.Count];
        for (int index = 0; index < references.Length; index++)
        {
            references[index] = new ResourceManifestReference
            {
                Resource = resources[index],
                Application = "appa",
                Endpoints = ["api"],
                Manifest = "Assimalign.Cohesion.Test.ApplicationModel",
            };
        }

        return references;
    }

    private sealed class LoopbackSecretStoreGateway : ApplicationGateway
    {
        private readonly IReadOnlyList<IApplicationResourceController> _controllers;
        private readonly IApplicationResourceStateManager _state;

        internal LoopbackSecretStoreGateway(
            IApplicationResourceStateManager state,
            IApplicationResourceController controller,
            ApplicationGatewayOptions options)
            : base(options)
        {
            _state = state;
            _controllers = [controller];
        }

        public override ResourceName Name => (ResourceName)"gateway-e2e";

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
            CancellationToken cancellationToken) => Task.CompletedTask;

        protected override Task RemoveApplicationExportAsync(
            ApplicationName application,
            CancellationToken cancellationToken) => Task.CompletedTask;

        private sealed class TestArtifact(ResourceId resource) : IResourceArtifact
        {
            public ResourceId Resource { get; } = resource;
        }
    }

    private sealed class LoopbackSecretStoreController : IApplicationResourceController, IAsyncDisposable
    {
        private readonly string _dataPath;
        private readonly Uri _endpoint;
        private SecretStoreApplication? _application;

        internal LoopbackSecretStoreController(Uri endpoint, string dataPath)
        {
            _endpoint = endpoint;
            _dataPath = dataPath;
        }

        internal ResourceInputs? StoreInputs { get; private set; }

        internal ResourceInputs? DependentInputs { get; private set; }

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
            if (string.Equals(context.Plan.Kind, "SecretStore", StringComparison.Ordinal))
            {
                StoreInputs = context.Inputs;
                if (_application is null)
                {
                    string token = Encoding.ASCII.GetString(
                        context.Inputs.BootstrapCredential.Span);
                    ResourceContext resourceContext = SecretStoreTestHost.CreateContext(
                        _endpoint,
                        _dataPath,
                        token,
                        context.Inputs.ApplicationTrustKey,
                        gatewayName: "gateway-e2e",
                        applicationName: context.Model.Name.ToString(),
                        resourceName: context.Resource.Name.ToString());
                    using (ResourceRuntime.CreateScope(resourceContext))
                    {
                        SecretStoreApplicationBuilder builder = SecretStoreTestHost.CreateBuilder();
                        builder.AddSecret(
                            "app/api-key",
                            Encoding.UTF8.GetBytes("gateway-secret"));
                        _application = builder.Build();
                    }

                    await ((IHost)_application).StartAsync(cancellationToken);
                }

                context.State.SetState(
                    context.Resource.Id,
                    ResourceLifecycle.Running,
                    observedEndpoints:
                    [
                        new ResourceEndpoint(
                            "api",
                            _endpoint.Scheme,
                            _endpoint.Port,
                            Host: _endpoint.Host),
                    ]);
                return;
            }

            DependentInputs = context.Inputs;
            context.State.SetState(context.Resource.Id, ResourceLifecycle.Running);
        }

        public async Task StopAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(context.Plan.Kind, "SecretStore", StringComparison.Ordinal))
            {
                await StopApplicationAsync(cancellationToken);
            }
        }

        public Task DeleteAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default) => StopAsync(context, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await StopApplicationAsync(CancellationToken.None);
        }

        private async Task StopApplicationAsync(CancellationToken cancellationToken)
        {
            if (_application is null)
            {
                return;
            }

            SecretStoreApplication application = _application;
            _application = null;
            await ((IHost)application).StopAsync(cancellationToken);
            await ((IAsyncDisposable)application).DisposeAsync();
        }
    }
}
