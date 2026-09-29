using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;
using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// Every credential the gateway mints goes through <see cref="ApplicationProviders.CredentialIssuer"/>
/// first and falls back to the default ES256 application-key issuer, whose output must stay exactly
/// what the gateway minted before credential issuers existed.
/// </summary>
public sealed class GatewayCredentialIssuerTests : IDisposable
{
    private const string displayPrefix = "Cohesion Test [ApplicationModel.Gateway] - Credential issuer: ";
    private const string gatewayName = "test-gateway";
    private const string developerName = "developer@example.test";
    private static readonly DateTimeOffset _now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _bootstrapLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan _developerLifetime = TimeSpan.FromHours(8);
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _keyId;
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(),
        "cohesion-gateway-credential-tests",
        Guid.NewGuid().ToString("N"))).FullName;

    public GatewayCredentialIssuerTests()
    {
        using var key = new GatewayTrustKey(ECDsa.Create(_signingKey.ExportParameters(includePrivateParameters: true)));
        _keyId = key.KeyId;
    }

    public void Dispose()
    {
        _signingKey.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory(DisplayName = displayPrefix + "The default issuer keeps every purpose's application-key credential byte for byte")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IssueCredential_DefaultIssuer_ShouldKeepEveryPurposeByteCompatible(bool registerDeferringIssuer)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(_testTimeout);
        RecordingCredentialIssuer? issuer = registerDeferringIssuer ? new RecordingCredentialIssuer() : null;

        // Act
        IssuedCredentials issued = await RunScenarioAsync(issuer, cancellation.Token);

        // Assert: each purpose equals the token the pre-issuer gateway signed for the same inputs.
        AssertDefaultCredential(issued.Bootstrap, "api", gatewayName, _bootstrapLifetime);
        AssertDefaultCredential(issued.Access, "api", gatewayName, _bootstrapLifetime);
        AssertDefaultCredential(issued.Telemetry, "logs", "api", _bootstrapLifetime, telemetry: true);
        AssertDefaultCredential(issued.PeerControlPlane, "cohesion-export", gatewayName, _developerLifetime, gatewayUse: true);
        AssertDefaultCredential(issued.RemoteCommand, "cohesion-export", gatewayName, _developerLifetime, gatewayUse: true);
        AssertDefaultCredential(issued.Developer, "cohesion-export", developerName, _developerLifetime);
        issued.TelemetryHeaders.ShouldBe("Authorization: Bearer " + issued.Telemetry + "\n");
        // One default token serves a resource's bootstrap and every gateway call to it in the pass.
        issued.Access.ShouldBe(issued.Bootstrap);

        if (issuer is not null)
        {
            ApplicationCredentialRequest[] requests = issuer.Requests.ToArray();
            requests.ShouldAllBe(request => request.Application == (ApplicationName)"appa" && request.Gateway == (ResourceName)gatewayName);
            requests.ShouldContain(request => Matches(request, ApplicationCredentialPurpose.ResourceBootstrap, "api", gatewayName, _bootstrapLifetime));
            requests.ShouldContain(request => Matches(request, ApplicationCredentialPurpose.ResourceBootstrap, "logs", gatewayName, _bootstrapLifetime));
            requests.ShouldContain(request => Matches(request, ApplicationCredentialPurpose.ResourceAccess, "api", gatewayName, _bootstrapLifetime));
            requests.ShouldContain(request => Matches(request, ApplicationCredentialPurpose.Telemetry, "logs", "api", _bootstrapLifetime));
            requests.ShouldContain(request => Matches(request, ApplicationCredentialPurpose.PeerControlPlane, "cohesion-export", gatewayName, _developerLifetime));
            requests.ShouldContain(request => Matches(request, ApplicationCredentialPurpose.RemoteCommand, "cohesion-export", gatewayName, _developerLifetime));
            requests.ShouldContain(request => Matches(request, ApplicationCredentialPurpose.Developer, "cohesion-export", developerName, _developerLifetime));
        }
    }

    [Fact(DisplayName = displayPrefix + "A registered issuer mints the purposes it handles and the default issuer the rest")]
    public async Task IssueCredential_IssuerForSomePurposes_ShouldFallBackForTheRest()
    {
        // Arrange: an identity provider issues bootstrap, telemetry (with its own scheme), and remote
        // command credentials, and defers resource access, peer discovery, and developer tokens.
        using var cancellation = new CancellationTokenSource(_testTimeout);
        var issuer = new RecordingCredentialIssuer(request => request.Purpose switch
        {
            ApplicationCredentialPurpose.ResourceBootstrap =>
                new ApplicationCredential("Bearer", $"idp-bootstrap-{request.Audience}", _now.AddHours(1)),
            ApplicationCredentialPurpose.Telemetry =>
                new ApplicationCredential("DPoP", $"idp-telemetry-{request.Audience}-{request.Subject}", _now.AddHours(1)),
            ApplicationCredentialPurpose.RemoteCommand =>
                new ApplicationCredential("bearer", "idp-remote-command", _now.AddHours(1)),
            _ => null,
        });

        // Act
        IssuedCredentials issued = await RunScenarioAsync(issuer, cancellation.Token);

        // Assert
        issued.Bootstrap.ShouldBe("idp-bootstrap-api");
        issued.TelemetryHeaders.ShouldBe("Authorization: DPoP idp-telemetry-logs-api\n");
        issued.RemoteCommand.ShouldBe("idp-remote-command");
        AssertDefaultCredential(issued.Access, "api", gatewayName, _bootstrapLifetime);
        AssertDefaultCredential(issued.PeerControlPlane, "cohesion-export", gatewayName, _developerLifetime, gatewayUse: true);
        AssertDefaultCredential(issued.Developer, "cohesion-export", developerName, _developerLifetime);
    }

    [Theory(DisplayName = displayPrefix + "A non-bearer credential for a bearer-only carrier is refused")]
    [InlineData(ApplicationCredentialPurpose.Developer, "DPoP", "idp-developer")]
    [InlineData(ApplicationCredentialPurpose.Developer, "Bearer", "idp\r\nX-Injected: yes")]
    [InlineData(ApplicationCredentialPurpose.ResourceBootstrap, "Basic", "idp-bootstrap")]
    public async Task IssueCredential_UnpresentableCredential_ShouldBeRefused(
        ApplicationCredentialPurpose purpose,
        string scheme,
        string value)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(_testTimeout);
        var issuer = new RecordingCredentialIssuer(request => request.Purpose == purpose
            ? new ApplicationCredential(scheme, value, _now.AddHours(1))
            : null);
        TestGateway gateway = CreateGateway(new CredentialCapturingController());
        IApplicationBuilder builder = Application
            .CreateBuilder((ApplicationName)"appa", ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(gateway);
        builder.AddResource(new TestResource("api"));
        builder.Providers.CredentialIssuer = issuer;
        IApplicationModel model = builder.Build().Model;

        try
        {
            // Act
            Exception? failure = purpose == ApplicationCredentialPurpose.Developer
                ? await Record.ExceptionAsync(() => gateway.IssueDeveloperTokenAsync(model, developerName, cancellation.Token))
                : await Record.ExceptionAsync(() => ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token));

            // Assert
            failure.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain(purpose.ToString(), Case.Sensitive);
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync(CancellationToken.None);
        }
    }

    [Theory(DisplayName = displayPrefix + "An expired cached resource credential is reissued instead of presented")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetResourceCommandCredential_CachedCredentialExpired_ShouldReissue(bool registerIssuer)
    {
        // Arrange: the served control plane dispatches with the pass cache long after the pass. The
        // identity provider's access credentials live one hour; the default issuer's live the
        // bootstrap lifetime.
        using var cancellation = new CancellationTokenSource(_testTimeout);
        var clock = new FixedUtcTimeProvider(_now);
        int minted = 0;
        RecordingCredentialIssuer? issuer = registerIssuer
            ? new RecordingCredentialIssuer(request => request.Purpose == ApplicationCredentialPurpose.ResourceAccess
                ? new ApplicationCredential(
                    "Bearer",
                    $"idp-access-{Interlocked.Increment(ref minted)}",
                    clock.GetUtcNow().AddHours(1))
                : null)
            : null;
        TimeSpan lifetime = registerIssuer ? TimeSpan.FromHours(1) : _bootstrapLifetime;
        var controller = new CredentialCapturingController();
        TestGateway gateway = CreateGateway(controller, clock: clock);
        IApplicationBuilder builder = Application
            .CreateBuilder((ApplicationName)"appa", ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(gateway);
        builder.AddResource(CreateManifest("api", "appa", "http", "http", 8080));
        if (issuer is not null)
        {
            builder.Providers.CredentialIssuer = issuer;
        }

        IApplicationModel model = builder.Build().Model;
        var credentials = (IResourceCommandCredentialProvider)gateway;
        IApplicationGateway control = gateway;

        try
        {
            await control.StartAsync(model, cancellation.Token);

            // Act
            string first = await credentials.GetResourceCommandCredentialAsync(model.Name, "api", cancellation.Token);
            clock.Advance(lifetime - TimeSpan.FromMinutes(1));
            string beforeExpiry = await credentials.GetResourceCommandCredentialAsync(model.Name, "api", cancellation.Token);
            clock.Advance(TimeSpan.FromMinutes(2));
            string afterExpiry = await credentials.GetResourceCommandCredentialAsync(model.Name, "api", cancellation.Token);

            // Assert
            beforeExpiry.ShouldBe(first);
            afterExpiry.ShouldNotBe(first);
            if (registerIssuer)
            {
                first.ShouldBe("idp-access-1");
                afterExpiry.ShouldBe("idp-access-2");
            }
            else
            {
                // Until it expires, the default access token is the pass's bootstrap token.
                first.ShouldBe(Encoding.UTF8.GetString(controller.Inputs["api"].BootstrapCredential.Span));
                using JsonDocument payload = JsonDocument.Parse(Base64Url.DecodeFromChars(afterExpiry.Split('.')[1]));
                payload.RootElement.GetProperty("iat").GetInt64().ShouldBe(clock.GetUtcNow().ToUnixTimeSeconds());
            }
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
        }
    }

    // One pass that exercises every mint site: a telemetry sink and an emitter that depends on it,
    // a remote reference discovered through a peer gateway with a command delivered to it, then a
    // resource-access credential and a developer token.
    private async Task<IssuedCredentials> RunScenarioAsync(
        RecordingCredentialIssuer? issuer,
        CancellationToken cancellationToken)
    {
        var peer = new RecordingPeerControlPlaneClient(CreatePeerExport());
        var controller = new CredentialCapturingController();
        TestGateway gateway = CreateGateway(controller, peer);
        IApplicationBuilder builder = Application
            .CreateBuilder((ApplicationName)"appa", ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(gateway);
        IApplicationResourceDescriptor logs = builder.AddResource(CreateManifest("logs", "appa", "otlp", "https", 4318));
        IApplicationResourceDescriptor api = builder.AddResource(CreateManifest("api", "appa", "http", "http", 8080));
        api.DependsOn(logs);
        IApplicationResourceDescriptor remote = builder.RemoteReference(
            new ExternalResourceDeclaration("remote-api", "peer", ["https"], optional: false, CreatePeerManifest()),
            options => options.Gateway(new Uri("https://peer.test:9443")));
        using JsonDocument payload = JsonDocument.Parse("{\"value\":\"configured\"}");
        builder.AddCommand(ResourceCommands.Create("test.apply", "setting", remote.Resource, "appa",
            payload.RootElement, GatewayCommandJsonContext.Default.JsonElement));
        builder.Providers.Telemetry = ResourceTelemetrySink.FromResource(logs);
        if (issuer is not null)
        {
            builder.Providers.CredentialIssuer = issuer;
        }

        IApplicationModel model = builder.Build().Model;
        IApplicationGateway control = gateway;
        try
        {
            await control.StartAsync(model, cancellationToken);
            string access = await ((IResourceCommandCredentialProvider)gateway)
                .GetResourceCommandCredentialAsync(model.Name, "api", cancellationToken);
            string developer = await gateway.IssueDeveloperTokenAsync(model, developerName, cancellationToken);
            IResourceTelemetry telemetry = controller.Telemetry["api"].ShouldNotBeNull();
            string headers = Encoding.UTF8.GetString(telemetry.HeadersDocument.Span);
            return new IssuedCredentials(
                Encoding.UTF8.GetString(controller.Inputs["api"].BootstrapCredential.Span),
                access,
                headers[(headers.LastIndexOf(' ') + 1)..].TrimEnd('\n'),
                headers,
                peer.DiscoveryCredentials.ShouldHaveSingleItem(),
                peer.CommandCredentials.ShouldHaveSingleItem(),
                developer);
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
        }
    }

    private TestGateway CreateGateway(
        IApplicationResourceController controller,
        IControlPlaneClient? peer = null,
        TimeProvider? clock = null)
    {
        var options = new ApplicationGatewayOptions
        {
            ExportDirectory = _root,
            TimeProvider = clock ?? new FixedUtcTimeProvider(_now),
            TrustKeyRepository = new FixedTrustKeyRepository(_signingKey),
            BootstrapCredentialLifetime = _bootstrapLifetime,
            DeveloperTokenLifetime = _developerLifetime,
            ControlPlaneClient = peer,
        };
        return new TestGateway(new InMemoryResourceStateManager(), [controller], options: options, name: gatewayName);
    }

    // The exact credential the gateway's default issuer signed before credential issuers existed,
    // written with that recipe's literal claim names. ES256 signatures and jti are random, so the
    // JOSE header must match byte for byte and the payload byte for byte once jti is masked.
    private void AssertDefaultCredential(
        string compact,
        string audience,
        string subject,
        TimeSpan lifetime,
        bool telemetry = false,
        bool gatewayUse = false)
    {
        var descriptor = new JsonWebTokenDescriptor
        {
            Id = Guid.NewGuid().ToString("N"),
            Issuer = "appa",
            Subject = new SubjectIdentifier(subject, issuer: "appa"),
            TokenType = "JWT",
            IssuedAt = _now,
            NotBefore = _now,
            ExpiresAt = _now.Add(lifetime),
        };
        descriptor.Audiences.Add(audience);
        if (telemetry)
        {
            descriptor.Claims.Add(new IdentityClaim("scope", "telemetry"));
        }
        if (gatewayUse)
        {
            descriptor.Claims.Add(new IdentityClaim("cohesion_token_use", "gateway"));
        }

        string expected = JsonWebTokenWriter.CreateEs256(_signingKey, _keyId).Write(descriptor);
        string[] actualParts = compact.Split('.');
        string[] expectedParts = expected.Split('.');
        actualParts.Length.ShouldBe(3);
        actualParts[0].ShouldBe(expectedParts[0]);
        MaskTokenId(actualParts[1]).ShouldBe(MaskTokenId(expectedParts[1]));
        JsonWebToken token = JsonWebToken.Parse(compact);
        token.Id.ShouldNotBeNullOrWhiteSpace();
        IJsonWebTokenSignatureVerifier verifier = JsonWebTokenSignatureVerifier.CreateEcdsa(_signingKey, _keyId);
        verifier.Verify(token.Algorithm!, Encoding.ASCII.GetBytes(token.SigningInput!), Base64Url.DecodeFromChars(token.Parts!.Signature))
            .ShouldBeTrue();
    }

    private static string MaskTokenId(string encodedPayload)
    {
        using JsonDocument payload = JsonDocument.Parse(Base64Url.DecodeFromChars(encodedPayload));
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty claim in payload.RootElement.EnumerateObject())
            {
                if (claim.NameEquals("jti"))
                {
                    writer.WriteString("jti", "<masked>");
                }
                else
                {
                    claim.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static bool Matches(
        ApplicationCredentialRequest request,
        ApplicationCredentialPurpose purpose,
        string audience,
        string subject,
        TimeSpan lifetime) =>
        request.Purpose == purpose &&
        request.Audience == audience &&
        request.Subject == subject &&
        request.Lifetime == lifetime;

    private static ApplicationExportDocument CreatePeerExport()
    {
        var peerGateway = new TestGateway(
            new InMemoryResourceStateManager(),
            [new CredentialCapturingController()],
            name: "peer-gateway");
        IApplicationBuilder builder = Application
            .CreateBuilder((ApplicationName)"peer", ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(peerGateway);
        builder.AddResource(CreatePeerManifest());
        using var peerKey = new GatewayTrustKey(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        return ApplicationExportDocument.Create(
            builder.Build().Model,
            "1",
            new Dictionary<ResourceName, IReadOnlyList<ApplicationExportEndpoint>>
            {
                ["remote-api"] = [new ApplicationExportEndpoint("https", "peer.test:443", "https://peer.test:443")],
            },
            trustKey: peerKey.PublicJwk);
    }

    private static ResourceManifest CreatePeerManifest() =>
        CreateManifest("remote-api", "peer", "https", "https", 443) with
        {
            Commands = [new ResourceManifestCommand("test.apply")],
        };

    private static ResourceManifest CreateManifest(
        string name,
        string application,
        string endpoint,
        string scheme,
        int port) => new()
        {
            Name = name,
            Application = application,
            Kind = "test",
            ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = name + ".dll",
                AppHost = name,
            },
            Endpoints =
            [
                new ResourceManifestEndpoint
                {
                    Name = endpoint,
                    Scheme = scheme,
                    Protocol = "tcp",
                    ContainerPort = port,
                },
            ],
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = endpoint,
                Path = "/cohesion/v1",
            },
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = WorkloadKind.Deployment,
                Replicas = 1,
                RestartPolicy = "Never",
            },
        };

    private sealed record IssuedCredentials(
        string Bootstrap,
        string Access,
        string Telemetry,
        string TelemetryHeaders,
        string PeerControlPlane,
        string RemoteCommand,
        string Developer);
}
