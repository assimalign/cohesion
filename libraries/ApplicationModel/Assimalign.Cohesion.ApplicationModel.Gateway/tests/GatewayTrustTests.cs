using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public sealed class GatewayTrustTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - LoadOrCreateAsync: Should persist a distinct P-256 trust key per application")]
    public async Task LoadOrCreateAsync_WhenReloaded_ShouldPersistApplicationTrustKey()
    {
        // Arrange
        string root = CreateTestDirectory();
        using var cancellation = new CancellationTokenSource(TestTimeout);
        ApplicationName applicationA = ApplicationName.Parse("appa");
        ApplicationName applicationB = ApplicationName.Parse("appb");
        ResourceName gateway = "test-gateway";

        try
        {
            // Act
            using var first = new GatewayTrustKey(await new GatewayTrustKeyStore(root)
                .LoadOrCreateAsync(applicationA, gateway, cancellation.Token));
            using var reloaded = new GatewayTrustKey(await new GatewayTrustKeyStore(root)
                .LoadOrCreateAsync(applicationA, gateway, cancellation.Token));
            using var otherApplication = new GatewayTrustKey(await new GatewayTrustKeyStore(root)
                .LoadOrCreateAsync(applicationB, gateway, cancellation.Token));

            // Assert
            first.PrivateKey.KeySize.ShouldBe(256);
            reloaded.PrivateKey.KeySize.ShouldBe(256);
            reloaded.KeyId.ShouldBe(first.KeyId);
            reloaded.PublicJwk.GetRawText().ShouldBe(first.PublicJwk.GetRawText());
            otherApplication.KeyId.ShouldNotBe(first.KeyId);
            AssertPublicJwk(first.PublicJwk, first.KeyId);
            File.Exists(Path.Combine(
                root,
                applicationA.ToString(),
                "trust",
                gateway.ToString(),
                "private-key.p8.protected")).ShouldBeTrue();
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - ReconcileAsync: Should rotate an audience-bound ES256 bootstrap token on every pass")]
    public async Task ReconcileAsync_OnEveryPass_ShouldRotateValidEs256BootstrapToken()
    {
        // Arrange
        string root = CreateTestDirectory();
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var options = new ApplicationGatewayOptions
        {
            ExportDirectory = root,
            BootstrapCredentialLifetime = TimeSpan.FromHours(24),
        };
        var controller = new InputHistoryController();
        var gateway = new TestGateway(
            new InMemoryResourceStateManager(),
            [controller],
            options: options,
            name: "test-gateway");
        IApplicationModel model = BuildModel(gateway, "appa", "secret-store");
        IApplicationGateway control = gateway;
        bool started = false;

        try
        {
            // Act
            await control.StartAsync(model, cancellation.Token);
            started = true;
            await control.ReconcileAsync(model, cancellation.Token);

            // Assert
            controller.Inputs.Count.ShouldBe(2);
            ResourceInputs firstInputs = controller.Inputs[0];
            ResourceInputs secondInputs = controller.Inputs[1];
            string firstCompact = Encoding.ASCII.GetString(firstInputs.BootstrapCredential.Span);
            string secondCompact = Encoding.ASCII.GetString(secondInputs.BootstrapCredential.Span);
            JsonWebToken first = JsonWebToken.Parse(firstCompact);
            JsonWebToken second = JsonWebToken.Parse(secondCompact);

            firstCompact.ShouldNotBe(secondCompact);
            first.Id.ShouldNotBeNullOrWhiteSpace();
            second.Id.ShouldNotBeNullOrWhiteSpace();
            second.Id.ShouldNotBe(first.Id);
            AssertToken(first, "secret-store", TimeSpan.FromHours(24));
            AssertToken(second, "secret-store", TimeSpan.FromHours(24));

            firstInputs.ApplicationTrustKey.ToArray()
                .ShouldBe(secondInputs.ApplicationTrustKey.ToArray());
            using JsonDocument publicJwkDocument = JsonDocument.Parse(
                firstInputs.ApplicationTrustKey);
            JsonElement publicJwk = publicJwkDocument.RootElement;
            AssertPublicJwk(publicJwk, first.Header.KeyId.ShouldNotBeNull());
            VerifyWithPublicJwk(first, publicJwk).ShouldBeTrue();
            VerifyWithPublicJwk(second, publicJwk).ShouldBeTrue();
        }
        finally
        {
            if (started)
            {
                await control.StopAsync(CancellationToken.None);
            }

            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - IssueDeveloperTokenAsync: Should issue an ES256 cohesion-export token bounded to eight hours")]
    public async Task IssueDeveloperTokenAsync_WhenRequested_ShouldIssueBoundedExportToken()
    {
        // Arrange
        string root = CreateTestDirectory();
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var options = new ApplicationGatewayOptions
        {
            ExportDirectory = root,
            DeveloperTokenLifetime = TimeSpan.FromHours(8),
        };
        var gateway = new TestGateway(
            new InMemoryResourceStateManager(),
            [new InputHistoryController()],
            options: options,
            name: "test-gateway");
        IApplicationModel model = BuildModel(gateway, "appa", "api");
        IApplicationTrustGateway trustGateway = gateway;

        try
        {
            // Act
            string compact = await trustGateway.IssueDeveloperTokenAsync(
                model,
                "developer@example.test",
                cancellation.Token);
            JsonWebToken token = JsonWebToken.Parse(compact);
            IReadOnlyList<TrustedIssuer> issuers = trustGateway.GetTrustedIssuers(model.Name);

            // Assert
            token.Id.ShouldNotBeNullOrWhiteSpace();
            AssertToken(token, "cohesion-export", TimeSpan.FromHours(8));
            token.Subject.ShouldNotBeNull().Value.ShouldBe("developer@example.test");
            issuers.Count.ShouldBe(1);
            issuers[0].Issuer.ShouldBe("appa");
            VerifyWithPublicJwk(token, issuers[0].PublicKey).ShouldBeTrue();
        }
        finally
        {
            // IssueDeveloperTokenAsync initializes trust without opening a model session;
            // StopAsync clears that owned key in both the success and assertion-failure paths.
            await ((IApplicationGateway)gateway).StopAsync(CancellationToken.None);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Trust repository: Platform override owns load and rotation")]
    public async Task TrustKeyRepository_WhenConfigured_ShouldOwnPersistenceAndRotation()
    {
        // Arrange
        string root = CreateTestDirectory();
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var repository = new RecordingTrustKeyRepository();
        var options = new ApplicationGatewayOptions
        {
            ExportDirectory = root,
            TrustKeyRepository = repository,
        };
        var gateway = new TestGateway(
            new InMemoryResourceStateManager(),
            [new InputHistoryController()],
            options: options,
            name: "test-gateway");
        IApplicationModel model = BuildModel(gateway, "appa", "api");
        IApplicationTrustGateway trustGateway = gateway;

        try
        {
            // Act
            JsonWebToken first = JsonWebToken.Parse(await trustGateway.IssueDeveloperTokenAsync(
                model,
                "developer@example.test",
                cancellation.Token));
            await trustGateway.RotateTrustKeyAsync(model, cancellation.Token);
            JsonWebToken second = JsonWebToken.Parse(await trustGateway.IssueDeveloperTokenAsync(
                model,
                "developer@example.test",
                cancellation.Token));

            // Assert
            repository.LoadCount.ShouldBe(1);
            repository.RotateCount.ShouldBe(1);
            second.Header.KeyId.ShouldNotBe(first.Header.KeyId);
            File.Exists(Path.Combine(root, "appa", "trust", "test-gateway", "private-key.p8.protected"))
                .ShouldBeFalse();
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync(CancellationToken.None);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - TrustedIssuers: Loads peer keys from the application's own SecretStore")]
    public async Task StartAsync_WithOwnSecretStore_ShouldLoadApplicationTrustedIssuers()
    {
        // Arrange
        string root = CreateTestDirectory();
        using var cancellation = new CancellationTokenSource(TestTimeout);
        using var peerKey = new GatewayTrustKey(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        byte[] document = TrustedIssuerDocument.Write(
            [new TrustedIssuer("peer", peerKey.PublicJwk)]);
        var client = new TrustedIssuerStoreClient(document);
        var options = new ApplicationGatewayOptions
        {
            ExportDirectory = root,
            StoreClient = client,
        };
        var gateway = new TestGateway(
            new InMemoryResourceStateManager(),
            [new TrustStoreController()],
            options: options,
            name: "test-gateway");
        IApplicationBuilder builder = Application
            .CreateBuilder(ApplicationName.Parse("appa"), ["--environment", "Production"])
            .UseGateway(gateway);
        builder.AddResource(CreateSecretStoreManifest());
        IApplicationModel model = builder.Build().Model;
        IApplicationGateway control = gateway;
        bool started = false;

        try
        {
            // Act
            await control.StartAsync(model, cancellation.Token);
            started = true;
            IReadOnlyList<TrustedIssuer> issuers = gateway.GetTrustedIssuers(model.Name);

            // Assert
            issuers.Count.ShouldBe(2);
            issuers[0].Issuer.ShouldBe("appa");
            issuers[1].Issuer.ShouldBe("peer");
            client.Path.ShouldBe("trusted-issuers.json");
            JsonWebToken.Parse(client.Credential.ShouldNotBeNull()).Audiences.ShouldBe(["secrets"]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(document);
            if (started)
            {
                await control.StopAsync(CancellationToken.None);
            }

            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - TrustedIssuers: Development store absence preserves the local fallback")]
    public async Task StartAsync_WhenDevelopmentStoreHasNoDocument_ShouldPreserveLocalFallback()
    {
        // Arrange
        string root = CreateTestDirectory();
        using var cancellation = new CancellationTokenSource(TestTimeout);
        using var peerKey = new GatewayTrustKey(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var fallback = new TrustedIssuer("peer", peerKey.PublicJwk);
        await TrustedIssuerDocument.WriteFileAsync(
            Path.Combine(root, "appa", "trust", "trusted-issuers.json"),
            [fallback],
            cancellation.Token);
        var client = new TrustedIssuerStoreClient(Array.Empty<byte>()) { ReturnNotFound = true };
        var gateway = new TestGateway(
            new InMemoryResourceStateManager(),
            [new TrustStoreController()],
            options: new ApplicationGatewayOptions
            {
                ExportDirectory = root,
                StoreClient = client,
            },
            name: "test-gateway");
        IApplicationBuilder builder = Application
            .CreateBuilder(ApplicationName.Parse("appa"), ["--environment", "Development"])
            .UseGateway(gateway);
        builder.AddResource(CreateSecretStoreManifest());
        IApplicationModel model = builder.Build().Model;
        IApplicationGateway control = gateway;
        bool started = false;

        try
        {
            // Act
            await control.StartAsync(model, cancellation.Token);
            started = true;

            // Assert
            IReadOnlyList<TrustedIssuer> issuers = gateway.GetTrustedIssuers(model.Name);
            issuers.Count.ShouldBe(2);
            issuers[0].Issuer.ShouldBe("appa");
            issuers[1].Issuer.ShouldBe("peer");
        }
        finally
        {
            if (started)
            {
                await control.StopAsync(CancellationToken.None);
            }

            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Trust add: Platform endpoint seam supports a production one-shot grant")]
    public async Task AddTrustedIssuerAsync_WithPlatformStoreEndpoint_ShouldPersistProductionGrant()
    {
        // Arrange
        string root = CreateTestDirectory();
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var client = new TrustedIssuerStoreClient(Array.Empty<byte>());
        var options = new ApplicationGatewayOptions
        {
            ExportDirectory = root,
            StoreClient = client,
        };
        Uri storeEndpoint = new("https://secrets.appa.test:8443/");
        var gateway = new TestGateway(
            new InMemoryResourceStateManager(),
            [new InputHistoryController()],
            options: options,
            name: "test-gateway",
            ownSecretStoreEndpointResolver: (_, _) => storeEndpoint);
        IApplicationBuilder builder = Application
            .CreateBuilder(ApplicationName.Parse("appa"), ["--environment", "Production"])
            .UseGateway(gateway);
        builder.AddResource(CreateSecretStoreManifest());
        IApplicationModel model = builder.Build().Model;

        var peerGateway = new TestGateway(
            new InMemoryResourceStateManager(),
            [new InputHistoryController()],
            name: "peer-gateway");
        IApplicationModel peerModel = BuildModel(peerGateway, "peer", "api");
        using var peerKey = new GatewayTrustKey(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        ApplicationExportDocument export = ApplicationExportDocument.Create(
            peerModel,
            "1",
            trustKey: peerKey.PublicJwk);

        try
        {
            // Act
            await gateway.AddTrustedIssuerAsync(model, "peer", export, cancellation.Token);

            // Assert
            client.StoredEndpoint.ShouldBe(storeEndpoint);
            client.StoredIssuer.ShouldBe("peer");
            JsonWebToken.Parse(client.StoredCredential.ShouldNotBeNull()).Audiences.ShouldBe(["secrets"]);
            gateway.GetTrustedIssuers(model.Name).Count.ShouldBe(2);
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync(CancellationToken.None);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - TrustedIssuer: Rejects a JWK without the required ES256 public-key shape")]
    public void Constructor_InvalidPublicJwk_ShouldThrow()
    {
        // Arrange
        using JsonDocument document = JsonDocument.Parse("{}");

        // Act
        Action create = () => _ = new TrustedIssuer("peer", document.RootElement);

        // Assert
        Should.Throw<ArgumentException>(create).Message.ShouldContain("kty");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - TrustedIssuer: Rejects off-curve P-256 coordinates")]
    public void Constructor_OffCurveCoordinates_ShouldThrow()
    {
        // Arrange
        string zeroCoordinate = Base64Url.EncodeToString(new byte[32]);
        using JsonDocument document = JsonDocument.Parse(
            $$"""{"kty":"EC","crv":"P-256","x":"{{zeroCoordinate}}","y":"{{zeroCoordinate}}","kid":"invalid","alg":"ES256","use":"sig"}""");

        // Act
        Action create = () => _ = new TrustedIssuer("peer", document.RootElement);

        // Assert
        Should.Throw<ArgumentException>(create).Message.ShouldContain("valid P-256 point");
    }

    private static IApplicationModel BuildModel(
        IApplicationGateway gateway,
        string application,
        string resource)
    {
        IApplicationBuilder builder = Application
            .CreateBuilder(ApplicationName.Parse(application), ["--environment", "Development"])
            .UseGateway(gateway);
        builder.AddResource(new TestResource(resource));
        return builder.Build().Model;
    }

    private static ResourceManifest CreateSecretStoreManifest() => new()
    {
        Name = "secrets",
        Application = "appa",
        Kind = "SecretStore",
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
                Scheme = "https",
                Protocol = "tcp",
                ContainerPort = 8443,
            },
        ],
        ControlPlane = new ResourceManifestControlPlane
        {
            Endpoint = "api",
            Path = "/cohesion/v1",
        },
        Lifecycle = new ResourceManifestLifecycle
        {
            Workload = WorkloadKind.Deployment,
            Replicas = 1,
            RestartPolicy = "Never",
        },
    };

    private static void AssertToken(
        JsonWebToken token,
        string audience,
        TimeSpan maximumLifetime)
    {
        token.Algorithm.ShouldBe("ES256");
        token.Header.KeyId.ShouldNotBeNullOrWhiteSpace();
        token.Audiences.ShouldBe([audience]);
        token.IssuedAt.HasValue.ShouldBeTrue();
        token.ExpiresAt.HasValue.ShouldBeTrue();

        TimeSpan lifetime = token.ExpiresAt!.Value - token.IssuedAt!.Value;
        lifetime.ShouldBeGreaterThan(TimeSpan.Zero);
        lifetime.ShouldBeLessThanOrEqualTo(maximumLifetime);
    }

    private static void AssertPublicJwk(JsonElement publicJwk, string expectedKeyId)
    {
        publicJwk.ValueKind.ShouldBe(JsonValueKind.Object);
        publicJwk.GetProperty("kty").GetString().ShouldBe("EC");
        publicJwk.GetProperty("crv").GetString().ShouldBe("P-256");
        publicJwk.GetProperty("alg").GetString().ShouldBe("ES256");
        publicJwk.GetProperty("use").GetString().ShouldBe("sig");
        publicJwk.GetProperty("kid").GetString().ShouldBe(expectedKeyId);
        publicJwk.TryGetProperty("d", out _).ShouldBeFalse();
    }

    private static bool VerifyWithPublicJwk(JsonWebToken token, JsonElement publicJwk)
    {
        byte[] x = Base64Url.DecodeFromChars(
            publicJwk.GetProperty("x").GetString().ShouldNotBeNull());
        byte[] y = Base64Url.DecodeFromChars(
            publicJwk.GetProperty("y").GetString().ShouldNotBeNull());
        try
        {
            using ECDsa publicKey = ECDsa.Create();
            publicKey.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = x,
                    Y = y,
                },
            });

            IJsonWebTokenSignatureVerifier verifier = JsonWebTokenSignatureVerifier.CreateEcdsa(
                publicKey,
                publicJwk.GetProperty("kid").GetString());
            string algorithm = token.Algorithm.ShouldNotBeNull();
            byte[] signingInput = Encoding.ASCII.GetBytes(token.SigningInput.ShouldNotBeNull());
            byte[] signature = Base64Url.DecodeFromChars(
                token.Parts.ShouldNotBeNull().Signature);
            return verifier.CanVerify(algorithm, token.Header.KeyId) &&
                verifier.Verify(algorithm, signingInput, signature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(x);
            CryptographicOperations.ZeroMemory(y);
        }
    }

    private static string CreateTestDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "cohesion-gateway-trust-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTestDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class InputHistoryController : IApplicationResourceController
    {
        public List<ResourceInputs> Inputs { get; } = new();

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
            Inputs.Add(context.Inputs.ShouldNotBeNull());
            context.State.SetState(context.Resource.Id, ResourceLifecycle.Running);
            return Task.CompletedTask;
        }

        public Task StopAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.State.SetState(context.Resource.Id, ResourceLifecycle.Stopped);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default) => StopAsync(context, cancellationToken);
    }

    private sealed class TrustStoreController : IApplicationResourceController
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
            cancellationToken.ThrowIfCancellationRequested();
            context.State.SetState(
                context.Resource.Id,
                ResourceLifecycle.Running,
                observedEndpoints: [new ResourceEndpoint("api", "https", 8443, Host: "127.0.0.1")]);
            return Task.CompletedTask;
        }

        public Task StopAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.State.SetState(context.Resource.Id, ResourceLifecycle.Stopped);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default) => StopAsync(context, cancellationToken);
    }

    private sealed class TrustedIssuerStoreClient(byte[] document) : IGatewayStoreClient
    {
        public bool ReturnNotFound { get; init; }

        public string? Credential { get; private set; }

        public string? Path { get; private set; }

        public Uri? StoredEndpoint { get; private set; }

        public string? StoredCredential { get; private set; }

        public string? StoredIssuer { get; private set; }

        public ValueTask<ReadOnlyMemory<byte>> ReadSecretAsync(
            Uri endpoint,
            string credential,
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            endpoint.ShouldBe(new Uri("https://127.0.0.1:8443/"));
            Credential = credential;
            Path = path;
            if (ReturnNotFound)
            {
                return ValueTask.FromException<ReadOnlyMemory<byte>>(
                    new System.Net.Http.HttpRequestException(
                        "TrustedIssuers is absent.",
                        inner: null,
                        System.Net.HttpStatusCode.NotFound));
            }

            return ValueTask.FromResult<ReadOnlyMemory<byte>>(document);
        }

        public ValueTask<string> ReadCertificateAsync(
            Uri endpoint,
            string credential,
            string name,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<string>(new NotSupportedException());

        public ValueTask<IReadOnlyDictionary<string, string?>> ReadConfigurationAsync(
            Uri endpoint,
            string credential,
            string name,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IReadOnlyDictionary<string, string?>>(new NotSupportedException());

        public ValueTask StoreTrustedIssuerAsync(
            Uri endpoint,
            string credential,
            string owner,
            string issuer,
            ReadOnlyMemory<byte> publicKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StoredEndpoint = endpoint;
            StoredCredential = credential;
            StoredIssuer = issuer;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingTrustKeyRepository : IGatewayTrustKeyRepository
    {
        public int LoadCount { get; private set; }

        public int RotateCount { get; private set; }

        public Task<ECDsa> LoadOrCreateAsync(
            ApplicationName application,
            ResourceName gateway,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            return Task.FromResult(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        }

        public Task<ECDsa> RotateAsync(
            ApplicationName application,
            ResourceName gateway,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RotateCount++;
            return Task.FromResult(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        }
    }
}
