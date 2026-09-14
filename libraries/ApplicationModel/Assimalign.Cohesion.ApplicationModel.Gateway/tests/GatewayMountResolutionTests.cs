using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public sealed class GatewayMountResolutionTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Mount sources: Store references resolve through observed endpoints and bootstrap credentials")]
    public async Task StartAsync_StoreReferences_ShouldResolveThroughObservedEndpoints()
    {
        // Arrange
        var client = new RecordingStoreClient();
        var options = CreateOptions(client);
        var state = new InMemoryResourceStateManager();
        var controller = new StoreEndpointController();
        var gateway = new TestGateway(state, [controller], options: options);
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(gateway);
        IApplicationResourceDescriptor secrets = builder.AddResource(CreateManifest("secrets", "SecretStore"));
        IApplicationResourceDescriptor configuration = builder.AddResource(
            CreateManifest("configuration", "ConfigurationStore"));
        IApplicationResourceDescriptor api = builder.AddResource(CreateManifest(
            "api",
            "Web",
            mounts:
            [
                CreateMount("api-key", ResourceMountKind.Secret, "secrets:api-key"),
                CreateMount("features", ResourceMountKind.Configuration, "configuration:features"),
            ],
            referenceResources: ["secrets", "configuration"]));
        api.DependsOn(secrets);
        api.DependsOn(configuration);
        IApplicationModel model = builder.Build().Model;

        // Act
        await ((IApplicationGateway)gateway).StartAsync(model);

        // Assert
        ResourceInputs inputs = controller.TargetInputs.ShouldNotBeNull();
        Encoding.UTF8.GetString(inputs.Mounts["api-key"].Content.Span).ShouldBe("store-secret");
        Encoding.UTF8.GetString(inputs.Mounts["features"].Content.Span)
            .ShouldBe("{\"alpha\":\"on\",\"zeta\":null}");
        client.SecretEndpoint.ShouldBe(new Uri("http://127.0.0.1:5101/"));
        client.ConfigurationEndpoint.ShouldBe(new Uri("http://127.0.0.1:5102/"));
        string secretCredential = client.SecretCredential.ShouldNotBeNull();
        string configurationCredential = client.ConfigurationCredential.ShouldNotBeNull();
        secretCredential.ShouldNotBeNullOrWhiteSpace();
        configurationCredential.ShouldNotBeNullOrWhiteSpace();
        JsonWebToken.Parse(secretCredential).Audiences.ShouldBe(["secrets"]);
        JsonWebToken.Parse(configurationCredential).Audiences.ShouldBe(["configuration"]);
        secretCredential.ShouldBe(Encoding.ASCII.GetString(
            controller.Inputs["secrets"].BootstrapCredential.Span));
        configurationCredential.ShouldBe(Encoding.ASCII.GetString(
            controller.Inputs["configuration"].BootstrapCredential.Span));
        client.SecretPath.ShouldBe("api-key");
        client.ConfigurationName.ShouldBe("features");

        await ((IApplicationGateway)gateway).StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Certificate mount: Missing leaf remains unresolved with a named error")]
    public async Task StartAsync_CertificateLeafUnavailable_ShouldReturnNamedUnresolvedInput()
    {
        // Arrange
        var client = new RecordingStoreClient { CertificateUnavailable = true };
        var options = CreateOptions(client);
        var state = new InMemoryResourceStateManager();
        var controller = new StoreEndpointController();
        var gateway = new TestGateway(state, [controller], options: options);
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(gateway);
        IApplicationResourceDescriptor secrets = builder.AddResource(CreateManifest("secrets", "SecretStore"));
        IApplicationResourceDescriptor api = builder.AddResource(CreateManifest(
            "api",
            "Web",
            mounts: [CreateMount("tls", ResourceMountKind.Secret, "secrets:certs/appa-api")],
            certificateMount: "tls",
            referenceResources: ["secrets"]));
        api.DependsOn(secrets);
        IApplicationModel model = builder.Build().Model;

        // Act
        await ((IApplicationGateway)gateway).StartAsync(model);

        // Assert
        ResourceMountInput input = controller.TargetInputs.ShouldNotBeNull().Mounts["tls"];
        input.IsResolved.ShouldBeFalse();
        input.UnresolvedReason.ShouldNotBeNull().ShouldContain("Certificate 'certs/appa-api'");
        input.UnresolvedReason.ShouldContain("mount 'tls'");
        input.UnresolvedReason.ShouldContain("not available", Case.Insensitive);
        client.CertificateName.ShouldBe("certs/appa-api");

        await ((IApplicationGateway)gateway).StopAsync();
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

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Certificate resolution: Explicit source, own store and development fallback have defined precedence")]
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
        string root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "mount-certs-" + Guid.NewGuid().ToString("N"))).FullName;
        var authority = new GatewayCertificateAuthority(Path.Combine(root, "fixture"), "fixture");
        string real = authority.Issue("supplied-api", []);
        using X509Certificate2 supplied = X509Certificate2.CreateFromPem(real, real);
        string pem = keys == 0 ? supplied.ExportCertificatePem() : real;
        if (keys == 2)
        {
            pem += real[real.IndexOf("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal)..];
        }
        var client = new RecordingStoreClient { CertificatePem = pem, RootPem = Encoding.UTF8.GetString(authority.ExportAnchors().Span) };
        var options = CreateOptions(client);
        options.ExportDirectory = root;
        options.Parameters["certificate"] = pem;
        var controller = new StoreEndpointController();
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [controller], options: options);
        IApplicationBuilder builder = Application.CreateBuilder("appa", ["--environment", AppEnvironment.Keys.Local]).UseGateway(gateway);
        IApplicationResourceDescriptor? secrets = branch == "development" ? null : builder.AddResource(CreateManifest("secrets", "SecretStore"));
        string? source = branch switch { "parameter" => "parameter:certificate", "store" => "secrets:certs/supplied-api", _ => null };
        IApplicationResourceDescriptor api = builder.AddResource(CreateManifest("api", "Web",
            mounts: [new ResourceManifestMount { Name = "tls", Kind = ResourceMountKind.Secret, ContainerPath = "/cohesion/mounts/tls", Source = source }],
            certificateMount: "tls", referenceResources: secrets is null ? [] : ["secrets"]));
        if (secrets is not null)
        {
            api.DependsOn(secrets);
        }
        try
        {
            await ((IApplicationGateway)gateway).StartAsync(builder.Build().Model);
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
                    client.CertificateRequests.ShouldBeEmpty();
                }
                else
                {
                    actual.Thumbprint.ShouldBe(supplied.Thumbprint);
                    Encoding.UTF8.GetString(input.Content.Span).ShouldBe(pem);
                }
                if (branch == "parameter")
                {
                    client.CertificateRequests.ShouldBeEmpty();
                }
                if (branch == "default-store")
                {
                    client.CertificateRequests.ShouldContain("certs/api-api");
                    client.CertificateRequests.ShouldContain("ca/root");
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

    private static ApplicationGatewayOptions CreateOptions(IGatewayStoreClient client) => new()
    {
        StoreClient = client,
        ExportDirectory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
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

    private sealed class StoreEndpointController : IApplicationResourceController
    {
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
                    observedEndpoints: [new ResourceEndpoint("api", "http", port, Host: "127.0.0.1")]);
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

    private sealed class RecordingStoreClient : IGatewayStoreClient
    {
        public bool CertificateUnavailable { get; init; }

        public string CertificatePem { get; init; } = string.Empty;

        public string RootPem { get; init; } = string.Empty;

        public List<string> CertificateRequests { get; } = new();

        public Uri? SecretEndpoint { get; private set; }

        public Uri? ConfigurationEndpoint { get; private set; }

        public string? SecretCredential { get; private set; }

        public string? ConfigurationCredential { get; private set; }

        public string? SecretPath { get; private set; }

        public string? ConfigurationName { get; private set; }

        public string? CertificateName { get; private set; }

        public ValueTask<ReadOnlyMemory<byte>> ReadSecretAsync(
            Uri endpoint,
            string credential,
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SecretEndpoint = endpoint;
            SecretCredential = credential;
            if (string.Equals(path, "trusted-issuers.json", StringComparison.Ordinal))
            {
                return ValueTask.FromResult<ReadOnlyMemory<byte>>(
                    Encoding.UTF8.GetBytes("{\"issuers\":[]}"));
            }

            SecretPath = path;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes("store-secret"));
        }

        public ValueTask<string> ReadCertificateAsync(
            Uri endpoint,
            string credential,
            string name,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SecretEndpoint = endpoint;
            SecretCredential = credential;
            CertificateName = name;
            CertificateRequests.Add(name);
            return CertificateUnavailable
                ? ValueTask.FromException<string>(new NotSupportedException("Certificate issuance is unavailable from this test store."))
                : ValueTask.FromResult(name == "ca/root" ? RootPem : CertificatePem);
        }

        public ValueTask<IReadOnlyDictionary<string, string?>> ReadConfigurationAsync(
            Uri endpoint,
            string credential,
            string name,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfigurationEndpoint = endpoint;
            ConfigurationCredential = credential;
            ConfigurationName = name;
            IReadOnlyDictionary<string, string?> result = new Dictionary<string, string?>
            {
                ["zeta"] = null,
                ["alpha"] = "on",
            };
            return ValueTask.FromResult(result);
        }

        public ValueTask StoreTrustedIssuerAsync(
            Uri endpoint,
            string credential,
            string owner,
            string issuer,
            ReadOnlyMemory<byte> publicKey,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
