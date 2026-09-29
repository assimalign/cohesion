using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// Mount-source resolution through the providers a model registers. The manifest kinds here are
/// deliberately not area kinds: the gateway resolves stores only through registrations.
/// </summary>
public sealed class GatewayMountResolutionTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Mount sources: Store references resolve through registered providers and bootstrap credentials")]
    public async Task StartAsync_StoreReferences_ShouldResolveThroughRegisteredProviders()
    {
        // Arrange
        var secrets = new RecordingSourceProvider("KeyStore");
        var settings = new RecordingSourceProvider("SettingStore");
        var controller = new StoreEndpointController();
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [controller], options: CreateOptions());
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(gateway);
        IApplicationResourceDescriptor store = builder.AddResource(CreateManifest("secrets", "KeyStore"));
        IApplicationResourceDescriptor configuration = builder.AddResource(CreateManifest("configuration", "SettingStore"));
        IApplicationResourceDescriptor api = builder.AddResource(CreateManifest(
            "api",
            "Web",
            mounts:
            [
                CreateMount("api-key", ResourceMountKind.Secret, "secrets:api-key"),
                CreateMount("features", ResourceMountKind.Configuration, "configuration:features"),
            ],
            referenceResources: ["secrets", "configuration"]));
        api.DependsOn(store);
        api.DependsOn(configuration);
        builder.Providers.Sources["secrets"] = secrets;
        builder.Providers.Sources["configuration"] = settings;
        IApplicationModel model = builder.Build().Model;

        try
        {
            // Act
            await ((IApplicationGateway)gateway).StartAsync(model);

            // Assert
            ResourceInputs inputs = controller.TargetInputs.ShouldNotBeNull();
            Encoding.UTF8.GetString(inputs.Mounts["api-key"].Content.Span).ShouldBe("store-secret");
            Encoding.UTF8.GetString(inputs.Mounts["features"].Content.Span)
                .ShouldBe("{\"alpha\":\"on\",\"zeta\":null}");

            ResourceSourceRequest secretRequest = secrets.Requests.ShouldHaveSingleItem();
            secretRequest.Application.ShouldBe(ApplicationName.Parse("appa"));
            secretRequest.Consumer.ShouldBe((ResourceName)"api");
            secretRequest.Mount.ShouldBe("api-key");
            secretRequest.Kind.ShouldBe(ResourceMountKind.Secret);
            secretRequest.Key.ShouldBe("api-key");
            ResourceProviderConnection secretStore = secretRequest.Store.ShouldNotBeNull();
            secretStore.Caller.ShouldBe(ApplicationName.Parse("appa"));
            secretStore.Resource.ShouldBe((ResourceName)"secrets");
            secretStore.ResourceKind.ShouldBe("KeyStore");
            secretStore.ControlPlaneAddress.ShouldBe(new Uri("http://127.0.0.1:5101/cohesion/v1"));
            secretStore.ServerCertificateValidator.ShouldBeNull();
            JsonWebToken.Parse(secretStore.BearerCredential).Audiences.ShouldBe(["secrets"]);
            secretStore.BearerCredential.ShouldBe(Encoding.ASCII.GetString(
                controller.Inputs["secrets"].BootstrapCredential.Span));

            ResourceSourceRequest settingsRequest = settings.Requests.ShouldHaveSingleItem();
            settingsRequest.Kind.ShouldBe(ResourceMountKind.Configuration);
            settingsRequest.Key.ShouldBe("features");
            ResourceProviderConnection settingsStore = settingsRequest.Store.ShouldNotBeNull();
            settingsStore.ControlPlaneAddress.ShouldBe(new Uri("http://127.0.0.1:5102/cohesion/v1"));
            JsonWebToken.Parse(settingsStore.BearerCredential).Audiences.ShouldBe(["configuration"]);
            settingsStore.BearerCredential.ShouldBe(Encoding.ASCII.GetString(
                controller.Inputs["configuration"].BootstrapCredential.Span));
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Mount sources: A source outside the model is resolved without a connection")]
    public async Task StartAsync_NonModelSource_ShouldCallProviderWithoutConnection()
    {
        // Arrange
        var vault = new RecordingSourceProvider { Secret = Encoding.UTF8.GetBytes("vault-secret") };
        var controller = new StoreEndpointController();
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [controller], options: CreateOptions());
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                ["--environment", AppEnvironment.Keys.Production])
            .UseGateway(gateway);
        builder.AddResource(CreateManifest(
            "api",
            "Web",
            mounts: [CreateMount("db", ResourceMountKind.Secret, "vault:db-password")]));
        builder.Providers.Sources["vault"] = vault;
        IApplicationModel model = builder.Build().Model;

        try
        {
            // Act
            await ((IApplicationGateway)gateway).StartAsync(model);

            // Assert
            Encoding.UTF8.GetString(controller.TargetInputs.ShouldNotBeNull().Mounts["db"].Content.Span)
                .ShouldBe("vault-secret");
            ResourceSourceRequest request = vault.Requests.ShouldHaveSingleItem();
            request.Key.ShouldBe("db-password");
            request.Store.ShouldBeNull();
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Mount sources: A provider failure becomes a named unresolved input")]
    public async Task StartAsync_ProviderFailure_ShouldReturnNamedUnresolvedInput()
    {
        // Arrange
        var secrets = new RecordingSourceProvider("KeyStore") { Failure = new HttpRequestException("store offline") };
        var controller = new StoreEndpointController();
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [controller], options: CreateOptions());
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(gateway);
        IApplicationResourceDescriptor store = builder.AddResource(CreateManifest("secrets", "KeyStore"));
        builder.AddResource(CreateManifest(
                "api",
                "Web",
                mounts: [CreateMount("api-key", ResourceMountKind.Secret, "secrets:api-key")],
                referenceResources: ["secrets"]))
            .DependsOn(store);
        builder.Providers.Sources["secrets"] = secrets;
        IApplicationModel model = builder.Build().Model;

        try
        {
            // Act
            await ((IApplicationGateway)gateway).StartAsync(model);

            // Assert
            ResourceMountInput input = controller.TargetInputs.ShouldNotBeNull().Mounts["api-key"];
            input.IsResolved.ShouldBeFalse();
            input.UnresolvedReason.ShouldBe(
                "Mount 'api-key' on resource 'api' could not resolve 'secrets:api-key' through 'secrets': store offline");
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Mount sources: An imported model never inherits registrations and names the missing one")]
    public async Task StartAsync_ImportedModelStoreSource_ShouldNameMissingRegistration()
    {
        // Arrange: the composing builder registers the store, but a model imported from its
        // application-model document (an application-set member or an export) carries none.
        var secrets = new RecordingSourceProvider("KeyStore");
        var controller = new StoreEndpointController();
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [controller], options: CreateOptions());
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(gateway);
        IApplicationResourceDescriptor store = builder.AddResource(CreateManifest("secrets", "KeyStore"));
        builder.AddResource(CreateManifest(
                "api",
                "Web",
                mounts: [CreateMount("api-key", ResourceMountKind.Secret, "secrets:api-key")],
                referenceResources: ["secrets"]))
            .DependsOn(store);
        builder.Providers.Sources["secrets"] = secrets;
        IApplicationModel built = builder.Build().Model;
        IApplicationModel imported = ApplicationModelDocument.Create(built).ToModel();
        imported.Providers.ShouldBeSameAs(ApplicationProviders.Empty);

        try
        {
            // Act
            await ((IApplicationGateway)gateway).StartAsync(imported);

            // Assert
            ResourceMountInput input = controller.TargetInputs.ShouldNotBeNull().Mounts["api-key"];
            input.IsResolved.ShouldBeFalse();
            string reason = input.UnresolvedReason.ShouldNotBeNull();
            reason.ShouldContain("registers no provider for source 'secrets'", Case.Sensitive);
            reason.ShouldContain("imported from an application-model document", Case.Sensitive);
            reason.ShouldContain(
                "set.AddApplication(Applications.<Member>, application => application.Use<Area>(\"secrets\"))",
                Case.Sensitive);
            reason.ShouldContain("builder.Providers.Sources[\"secrets\"]", Case.Sensitive);
            secrets.Requests.ShouldBeEmpty();
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Mount sources: An HTTPS store read validates against the store owner's authority")]
    public async Task StartAsync_HttpsStoreSource_ShouldHandProviderTheOwnersTransportValidator()
    {
        // Arrange
        string root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "https-store-" + Guid.NewGuid().ToString("N"))).FullName;
        var secrets = new RecordingSourceProvider("KeyStore");
        var controller = new StoreEndpointController { Scheme = "https" };
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [controller], options: CreateOptions(root));
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(gateway);
        // The store's own source-free certificate comes from the Local development authority,
        // which gives the application's transport trust its anchor.
        IApplicationResourceDescriptor store = builder.AddResource(CreateManifest(
            "secrets",
            "KeyStore",
            mounts: [new ResourceManifestMount { Name = "tls", Kind = ResourceMountKind.Secret, ContainerPath = "/cohesion/mounts/tls" }],
            certificateMount: "tls",
            scheme: "https"));
        builder.AddResource(CreateManifest(
                "api",
                "Web",
                mounts: [CreateMount("cfg", ResourceMountKind.Secret, "secrets:key")],
                referenceResources: ["secrets"]))
            .DependsOn(store);
        builder.Providers.Sources["secrets"] = secrets;

        try
        {
            // Act
            await ((IApplicationGateway)gateway).StartAsync(builder.Build().Model);

            // Assert
            ResourceProviderConnection connection = secrets.Requests.ShouldHaveSingleItem().Store.ShouldNotBeNull();
            connection.ControlPlaneAddress.ShouldBe(new Uri("https://127.0.0.1:5101/cohesion/v1"));
            RemoteCertificateValidationCallback validator = connection.ServerCertificateValidator.ShouldNotBeNull();
            string ownPem = new GatewayCertificateAuthority(Path.Combine(root, "appa"), "appa").Issue("x", []);
            using X509Certificate2 own = X509Certificate2.CreateFromPem(ownPem, ownPem);
            validator(null!, own, null, SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeTrue();
            string unrelatedPem = new GatewayCertificateAuthority(Path.Combine(root, "other"), "other").Issue("x", []);
            using X509Certificate2 unrelated = X509Certificate2.CreateFromPem(unrelatedPem, unrelatedPem);
            validator(null!, unrelated, null, SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeFalse();
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Certificate mount: Missing leaf remains unresolved with a named error")]
    public async Task StartAsync_CertificateLeafUnavailable_ShouldReturnNamedUnresolvedInput()
    {
        // Arrange
        var secrets = new RecordingSourceProvider("KeyStore");
        var controller = new StoreEndpointController();
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [controller], options: CreateOptions());
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(gateway);
        IApplicationResourceDescriptor store = builder.AddResource(CreateManifest("secrets", "KeyStore"));
        IApplicationResourceDescriptor api = builder.AddResource(CreateManifest(
            "api",
            "Web",
            mounts: [CreateMount("tls", ResourceMountKind.Secret, "secrets:certs/appa-api")],
            certificateMount: "tls",
            referenceResources: ["secrets"]));
        api.DependsOn(store);
        builder.Providers.Sources["secrets"] = secrets;
        IApplicationModel model = builder.Build().Model;

        try
        {
            // Act
            await ((IApplicationGateway)gateway).StartAsync(model);

            // Assert
            ResourceMountInput input = controller.TargetInputs.ShouldNotBeNull().Mounts["tls"];
            input.IsResolved.ShouldBeFalse();
            input.UnresolvedReason.ShouldNotBeNull().ShouldContain("Certificate 'certs/appa-api'");
            input.UnresolvedReason.ShouldContain("mount 'tls'");
            input.UnresolvedReason.ShouldContain("not available", Case.Insensitive);
            secrets.Requests.ShouldHaveSingleItem().Key.ShouldBe("certs/appa-api");
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Gateway parameters: Command-line values override configured values")]
    public void Apply_CommandLineParameters_ShouldUseLastExplicitValue()
    {
        // Arrange
        var options = new ApplicationGatewayOptions();
        options.Parameters.Add("region", "configured");

        // Act
        ApplicationGatewayCommandLine.Apply(
            options,
            ["--parameter", "region=east", "--parameter=tenant=appa", "--parameter", "region=west"]);

        // Assert
        options.Parameters["region"].ShouldBe("west");
        options.Parameters["tenant"].ShouldBe("appa");
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Certificate resolution: Explicit source, registered authority and development fallback have defined precedence")]
    [InlineData("parameter", 1)]
    [InlineData("store", 1)]
    [InlineData("default-store", 1)]
    [InlineData("development", 1)]
    [InlineData("parameter", 0)]
    [InlineData("parameter", 2)]
    [InlineData("store", 0)]
    [InlineData("store", 2)]
    public async Task StartAsync_CertificateResolution_ShouldEnforceOrderAndShape(string branch, int keys)
    {
        // Arrange
        string root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "mount-certs-" + Guid.NewGuid().ToString("N"))).FullName;
        var authority = new GatewayCertificateAuthority(Path.Combine(root, "fixture"), "fixture");
        string real = authority.Issue("supplied-api", []);
        using X509Certificate2 supplied = X509Certificate2.CreateFromPem(real, real);
        string pem = keys == 0 ? supplied.ExportCertificatePem() : real;
        if (keys == 2)
        {
            pem += real[real.IndexOf("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal)..];
        }

        var certificate = new ResourceCertificate(pem, Encoding.UTF8.GetString(authority.ExportAnchors().Span));
        var source = new RecordingSourceProvider("KeyStore") { Certificate = certificate };
        var issuer = new RecordingCertificateAuthority(certificate, "KeyStore");
        ApplicationGatewayOptions options = CreateOptions(root);
        options.Parameters["certificate"] = pem;
        var controller = new StoreEndpointController();
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [controller], options: options);
        IApplicationBuilder builder = Application.CreateBuilder("appa", ["--environment", AppEnvironment.Keys.Local]).UseGateway(gateway);
        IApplicationResourceDescriptor? secrets = branch == "development" ? null : builder.AddResource(CreateManifest("secrets", "KeyStore"));
        string? mountSource = branch switch { "parameter" => "parameter:certificate", "store" => "secrets:certs/supplied-api", _ => null };
        IApplicationResourceDescriptor api = builder.AddResource(CreateManifest("api", "Web",
            mounts: [new ResourceManifestMount { Name = "tls", Kind = ResourceMountKind.Secret, ContainerPath = "/cohesion/mounts/tls", Source = mountSource }],
            certificateMount: "tls", referenceResources: secrets is null ? [] : ["secrets"]));
        if (secrets is not null)
        {
            api.DependsOn(secrets);
        }

        if (branch == "store")
        {
            builder.Providers.Sources["secrets"] = source;
        }

        if (branch == "default-store")
        {
            builder.Providers.CertificateAuthority = new ResourceProviderBinding<IResourceCertificateAuthority>("secrets", issuer);
        }

        try
        {
            // Act
            await ((IApplicationGateway)gateway).StartAsync(builder.Build().Model);

            // Assert
            ResourceInputs inputs = controller.TargetInputs.ShouldNotBeNull();
            ResourceMountInput input = inputs.Mounts["tls"];
            input.IsResolved.ShouldBe(keys == 1);
            if (keys != 1)
            {
                input.UnresolvedReason.ShouldNotBeNull().ShouldContain("mount 'tls'");
                input.UnresolvedReason.ShouldContain("exactly one");
            }
            else
            {
                using X509Certificate2 actual = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(input.Content.Span), Encoding.UTF8.GetString(input.Content.Span));
                if (branch == "development")
                {
                    actual.Thumbprint.ShouldNotBe(supplied.Thumbprint);
                    issuer.Requests.ShouldBeEmpty();
                }
                else
                {
                    actual.Thumbprint.ShouldBe(supplied.Thumbprint);
                    Encoding.UTF8.GetString(input.Content.Span).ShouldBe(pem);
                }

                if (branch == "parameter")
                {
                    source.Requests.ShouldBeEmpty();
                    issuer.Requests.ShouldBeEmpty();
                }

                if (branch == "default-store")
                {
                    (ResourceCertificateRequest request, ResourceProviderConnection? connection) = issuer.Requests.ShouldHaveSingleItem();
                    request.LeafName.ShouldBe("api-api");
                    request.Resource.ShouldBe((ResourceName)"api");
                    request.Endpoint.ShouldBe("api");
                    connection.ShouldNotBeNull().Resource.ShouldBe((ResourceName)"secrets");
                    connection.ControlPlaneAddress.ShouldBe(new Uri("http://127.0.0.1:5101/cohesion/v1"));
                }

                Encoding.UTF8.GetString(inputs.TrustBundle.Span).ShouldNotContain("PRIVATE KEY");
            }
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static ApplicationGatewayOptions CreateOptions(string? root = null) => new()
    {
        ExportDirectory = root ?? Path.Combine(
            Path.GetTempPath(),
            "cohesion-gateway-mount-tests",
            Guid.NewGuid().ToString("N")),
    };

    private static ResourceManifestMount CreateMount(
        string name,
        ResourceMountKind kind,
        string source) => new()
        {
            Name = name,
            ContainerPath = "/inputs/" + name,
            Kind = kind,
            Source = source,
        };

    private static ResourceManifest CreateManifest(
        string name,
        string kind,
        IReadOnlyList<ResourceManifestMount>? mounts = null,
        string? certificateMount = null,
        IReadOnlyList<string>? referenceResources = null,
        string scheme = "http") => new()
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
                    Scheme = scheme,
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

    private sealed class StoreEndpointController : IApplicationResourceController
    {
        public string Scheme { get; init; } = "http";

        public Dictionary<string, ResourceInputs> Inputs { get; } = new(StringComparer.Ordinal);

        public ResourceInputs? TargetInputs { get; private set; }

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
            string name = context.Resource.Name.ToString();
            Inputs[name] = context.Inputs;
            if (name is "secrets" or "configuration")
            {
                int port = name == "secrets" ? 5101 : 5102;
                context.State.SetState(
                    context.Resource.Id,
                    ResourceLifecycle.Running,
                    observedEndpoints: [new ResourceEndpoint("api", Scheme, port, Host: "127.0.0.1")]);
            }
            else
            {
                TargetInputs = context.Inputs;
                context.State.SetState(context.Resource.Id, ResourceLifecycle.Running);
            }

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
