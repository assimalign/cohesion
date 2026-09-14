using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;
using Shouldly;
using Xunit;

using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Tests;

public sealed partial class GatewayControlPlaneTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - GET: Should enforce trusted issuers and serve the shared export and observed resource")]
    public async Task Get_WithLoopbackListener_ShouldEnforceTrustAndServeDiscovery()
    {
        string root = CreateTestDirectory();
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var options = new ApplicationGatewayOptions
        {
            ExportDirectory = root,
            ControlPlane = GatewayControlPlane.CreateFactory(controlPlane =>
                controlPlane.MetadataDirectory = root),
        };
        var gateway = new TestGateway(options);
        IApplicationModel model = BuildModel(gateway, "appa", includeUnsupportedResource: false);
        var peerGateway = new TestGateway(new ApplicationGatewayOptions { ExportDirectory = root });
        IApplicationModel peerModel = BuildModel(peerGateway, "appb", includeUnsupportedResource: false);
        IApplicationGateway control = gateway;
        var client = new HttpClient();

        try
        {
            await control.StartAsync(model, cancellation.Token);
            string token = await ((IApplicationTrustGateway)gateway).IssueDeveloperTokenAsync(
                model,
                "developer",
                cancellation.Token);
            string peerToken = await ((IApplicationTrustGateway)peerGateway).IssueDeveloperTokenAsync(
                peerModel,
                "developer",
                cancellation.Token);
            (Uri address, JsonElement trustKey) = ReadMetadata(root, "appa");

            using HttpResponseMessage missing = await client.GetAsync(
                new Uri(address, "/cohesion/v1/application"),
                cancellation.Token);
            using HttpResponseMessage untrusted = await SendAsync(
                client,
                HttpMethod.Get,
                new Uri(address, "/cohesion/v1/application"),
                peerToken,
                content: null,
                cancellation.Token);
            TrustedIssuer peerIssuer = peerGateway.GetTrustedIssuers(peerModel.Name)[0];
            await ((IApplicationTrustGateway)gateway).AddTrustedIssuerAsync(
                model,
                "appb",
                ApplicationExportDocument.Create(peerModel, "1", trustKey: peerIssuer.PublicKey),
                cancellation.Token);
            using HttpResponseMessage application = await SendAsync(
                client,
                HttpMethod.Get,
                new Uri(address, "/cohesion/v1/application"),
                peerToken,
                content: null,
                cancellation.Token);
            using HttpResponseMessage resource = await SendAsync(
                client,
                HttpMethod.Get,
                new Uri(address, "/cohesion/v1/resources/api"),
                token,
                content: null,
                cancellation.Token);

            missing.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
            untrusted.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
            application.StatusCode.ShouldBe(NetHttpStatusCode.OK);
            await using (Stream applicationBody = await application.Content.ReadAsStreamAsync(cancellation.Token))
            {
                ApplicationExportDocument export = await ApplicationExportDocument.LoadAsync(
                    applicationBody,
                    cancellation.Token);
                export.Application.ShouldBe("appa");
                export.Model.Resources.Count.ShouldBe(1);
            }

            resource.StatusCode.ShouldBe(NetHttpStatusCode.OK);
            string resourceJson = await resource.Content.ReadAsStringAsync(cancellation.Token);
            resourceJson.ShouldContain("\"state\": \"Running\"");
            resourceJson.ShouldContain("http://127.0.0.1:43110");

            var issuer = new TrustedIssuer("appa", trustKey);
            IControlPlaneClient resolverClient = GatewayControlPlane.CreateClient(peerToken, issuer);
            IApplicationModel resolved = await ApplicationModelResolvers
                .Gateway(new Uri(address, "/gateway/base?ignored=yes#fragment"), resolverClient)
                .ResolveAsync(
                    new ApplicationModelResolutionContext(
                        model.Environment,
                        GatewayRunMode.Run,
                        "resolver"),
                    cancellation.Token);
            resolved.Name.ShouldBe(model.Name);
            resolved.Manifests.Count.ShouldBe(model.Manifests.Count);
        }
        finally
        {
            client.Dispose();
            await control.StopAsync(CancellationToken.None);
            File.Exists(Path.Combine(root, "appa", "control-plane.json")).ShouldBeFalse();
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Configure: Should serve only realizing modes and always install the resolver client")]
    public void Configure_WithGatewayRunModes_ShouldInstallExpectedDefaults()
    {
        var run = new ApplicationGatewayOptions();
        var apply = new ApplicationGatewayOptions();
        var bootstrap = new ApplicationGatewayOptions();
        var describe = new ApplicationGatewayOptions();
        var render = new ApplicationGatewayOptions();

        GatewayControlPlane.Configure(run, GatewayRunMode.Run);
        GatewayControlPlane.Configure(apply, GatewayRunMode.Apply);
        GatewayControlPlane.Configure(bootstrap, GatewayRunMode.Bootstrap);
        GatewayControlPlane.Configure(describe, GatewayRunMode.Describe);
        GatewayControlPlane.Configure(render, GatewayRunMode.Render);

        run.ControlPlane.ShouldNotBeNull();
        apply.ControlPlane.ShouldNotBeNull();
        bootstrap.ControlPlane.ShouldBeNull();
        describe.ControlPlane.ShouldBeNull();
        render.ControlPlane.ShouldBeNull();
        run.ControlPlaneClient.ShouldNotBeNull();
        apply.ControlPlaneClient.ShouldNotBeNull();
        bootstrap.ControlPlaneClient.ShouldNotBeNull();
        describe.ControlPlaneClient.ShouldNotBeNull();
        render.ControlPlaneClient.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Token profile: Should require typed bounded export credentials")]
    public void TokenProfile_WithSignedCredentials_ShouldEnforceTheExportLifetime()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        (string accepted, TrustedIssuer acceptedIssuer) = CreateControlPlaneToken(
            "caller",
            now,
            TimeSpan.FromHours(8),
            allowCommands: true);
        (string overlong, TrustedIssuer overlongIssuer) = CreateControlPlaneToken(
            "caller",
            now,
            TimeSpan.FromHours(8) + TimeSpan.FromSeconds(1),
            allowCommands: true);

        ControlPlaneTokenVerifier.TryVerify(
                accepted,
                [acceptedIssuer],
                now,
                out ControlPlanePrincipal principal)
            .ShouldBeTrue();
        principal.Issuer.ShouldBe("caller");
        principal.CanDispatchCommands.ShouldBeTrue();
        ControlPlaneTokenVerifier.TryVerify(
                overlong,
                [overlongIssuer],
                now,
                out _)
            .ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Commands: Should retain applied and rejected observations and dispatch deletion")]
    public async Task Commands_WithSupportedAndUnsupportedKinds_ShouldRecordOutcomes()
    {
        string root = CreateTestDirectory();
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var dispatcher = new RecordingCommandDispatcher();
        var options = new ApplicationGatewayOptions
        {
            ExportDirectory = root,
            ControlPlane = GatewayControlPlane.CreateFactory(controlPlane =>
            {
                controlPlane.MetadataDirectory = root;
                controlPlane.CommandDispatchers.Add(dispatcher);
            }),
        };
        var gateway = new TestGateway(options);
        IApplicationModel model = BuildModel(gateway, "appa", includeUnsupportedResource: true);
        var peerGateway = new TestGateway(new ApplicationGatewayOptions { ExportDirectory = root });
        IApplicationModel peerModel = BuildModel(peerGateway, "caller", includeUnsupportedResource: false);
        IApplicationGateway control = gateway;
        using var client = new HttpClient();

        try
        {
            await control.StartAsync(model, cancellation.Token);
            string token = await ((IApplicationTrustGateway)gateway).IssueDeveloperTokenAsync(
                model,
                "developer",
                cancellation.Token);
            (string commandToken, TrustedIssuer commandIssuer) = CreateControlPlaneToken(
                "caller",
                DateTimeOffset.UtcNow,
                TimeSpan.FromHours(1),
                allowCommands: true);
            ApplicationExportDocument peerExport = ApplicationExportDocument.Create(
                peerModel,
                "1",
                trustKey: commandIssuer.PublicKey);
            await ((IApplicationTrustGateway)gateway).AddTrustedIssuerAsync(
                model,
                "caller",
                peerExport,
                cancellation.Token);
            (Uri address, _) = ReadMetadata(root, "appa");

            using HttpResponseMessage developerWrite = await SendAsync(
                client,
                HttpMethod.Put,
                new Uri(address, "/cohesion/v1/resources/api/commands/developer-command"),
                token,
                Json("""
                    {"kind":"test.apply","owner":"appa","key":"developer-setting","payload":""}
                    """),
                cancellation.Token);
            using HttpResponseMessage applied = await SendAsync(
                client,
                HttpMethod.Put,
                new Uri(address, "/cohesion/v1/resources/api/commands/command-a"),
                commandToken,
                Json("""
                    {"kind":"test.apply","owner":"caller","key":"setting-a","payload":"aGVsbG8="}
                    """),
                cancellation.Token);
            using HttpResponseMessage replayed = await SendAsync(
                client,
                HttpMethod.Put,
                new Uri(address, "/cohesion/v1/resources/api/commands/command-a"),
                commandToken,
                Json("""
                    {"kind":"test.apply","owner":"caller","key":"setting-a","payload":"aGVsbG8="}
                    """),
                cancellation.Token);
            using HttpResponseMessage rejected = await SendAsync(
                client,
                HttpMethod.Put,
                new Uri(address, "/cohesion/v1/resources/worker/commands/command-b"),
                commandToken,
                Json("""
                    {"kind":"test.apply","owner":"caller","key":"setting-b","payload":""}
                    """),
                cancellation.Token);
            using HttpResponseMessage developerRead = await SendAsync(
                client,
                HttpMethod.Get,
                new Uri(address, "/cohesion/v1/resources/worker/commands"),
                token,
                content: null,
                cancellation.Token);
            using HttpResponseMessage observations = await SendAsync(
                client,
                HttpMethod.Get,
                new Uri(address, "/cohesion/v1/resources/worker/commands"),
                commandToken,
                content: null,
                cancellation.Token);
            using HttpResponseMessage deleted = await SendAsync(
                client,
                HttpMethod.Delete,
                new Uri(address, "/cohesion/v1/resources/api/commands/command-a"),
                commandToken,
                content: null,
                cancellation.Token);

            developerWrite.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
            applied.StatusCode.ShouldBe(NetHttpStatusCode.OK);
            replayed.StatusCode.ShouldBe(NetHttpStatusCode.OK);
            (await applied.Content.ReadAsStringAsync(cancellation.Token)).ShouldContain("\"status\": \"Applied\"");
            rejected.StatusCode.ShouldBe(NetHttpStatusCode.Conflict);
            string rejectedJson = await rejected.Content.ReadAsStringAsync(cancellation.Token);
            rejectedJson.ShouldContain("\"status\": \"Rejected\"");
            using (JsonDocument rejectedDocument = JsonDocument.Parse(rejectedJson))
            {
                rejectedDocument.RootElement.GetProperty("detail").GetString().ShouldBe(
                    "No command dispatcher is registered for resource kind 'unsupported'.");
            }
            developerRead.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
            observations.StatusCode.ShouldBe(NetHttpStatusCode.OK);
            (await observations.Content.ReadAsStringAsync(cancellation.Token)).ShouldContain("command-b");
            deleted.StatusCode.ShouldBe(NetHttpStatusCode.NoContent);
            dispatcher.Applied.ShouldBe(1);
            dispatcher.Deleted.ShouldBe(1);
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
            DeleteTestDirectory(root);
        }
    }

    private static IApplicationModel BuildModel(
        IApplicationGateway gateway,
        string application,
        bool includeUnsupportedResource)
    {
        IApplicationBuilder builder = Application
            .CreateBuilder(
                ApplicationName.Parse(application),
                ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(gateway);
        builder.AddResource(CreateManifest(application, "api", "test", 43110));
        if (includeUnsupportedResource)
        {
            builder.AddResource(CreateManifest(application, "worker", "unsupported", 43111));
        }

        return builder.Build().Model;
    }

    private static ResourceManifest CreateManifest(
        string application,
        string name,
        string kind,
        int port,
        string scheme = "http") =>
        new()
        {
            Name = name,
            Application = application,
            Kind = kind,
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
                    Name = "http",
                    Scheme = scheme,
                    Protocol = "tcp",
                    ContainerPort = port,
                },
            ],
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = "http",
                Path = "/cohesion/v1",
            },
            Commands = [new ResourceManifestCommand("test.apply")],
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = WorkloadKind.Deployment,
            },
        };

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        Uri address,
        string token,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, address)
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, cancellationToken);
    }

    private static StringContent Json(string value) =>
        new(value, Encoding.UTF8, "application/json");

    private static (string Token, TrustedIssuer Issuer) CreateControlPlaneToken(
        string issuer,
        DateTimeOffset issuedAt,
        TimeSpan lifetime,
        bool allowCommands)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters parameters = key.ExportParameters(includePrivateParameters: false);
        string encodedX = Base64Url.EncodeToString(parameters.Q.X!);
        string encodedY = Base64Url.EncodeToString(parameters.Q.Y!);
        byte[] canonicalKey = Encoding.UTF8.GetBytes(
            $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{encodedX}\",\"y\":\"{encodedY}\"}}");
        string keyId = Base64Url.EncodeToString(SHA256.HashData(canonicalKey));
        using JsonDocument publicKey = JsonDocument.Parse($$"""
            {
              "kty": "EC",
              "crv": "P-256",
              "x": "{{encodedX}}",
              "y": "{{encodedY}}",
              "kid": "{{keyId}}",
              "alg": "ES256",
              "use": "sig"
            }
            """);
        var descriptor = new JsonWebTokenDescriptor
        {
            Id = Guid.NewGuid().ToString("N"),
            Issuer = issuer,
            Subject = new SubjectIdentifier("gateway", issuer: issuer),
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            ExpiresAt = issuedAt.Add(lifetime),
        };
        descriptor.Audiences.Add("cohesion-export");
        if (allowCommands)
        {
            descriptor.Claims.Add(new IdentityClaim("cohesion_token_use", "gateway"));
        }

        string token = JsonWebTokenWriter.CreateEs256(key, keyId).Write(descriptor);
        return (token, new TrustedIssuer(issuer, publicKey.RootElement));
    }

    private static (Uri Address, JsonElement TrustKey) ReadMetadata(string root, string application)
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, application, "control-plane.json")));
        return (
            new Uri(document.RootElement.GetProperty("url").GetString()!, UriKind.Absolute),
            document.RootElement.GetProperty("trustKey").Clone());
    }

    private static string CreateTestDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "cohesion-control-plane-tests",
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

    private sealed class TestGateway : ApplicationGateway, IResourceTransportTrustProvider
    {
        private readonly InMemoryResourceStateManager _state = new();
        private readonly IReadOnlyList<IApplicationResourceController> _controllers =
            [new TestController()];

        public TestGateway(ApplicationGatewayOptions options)
            : base(options)
        {
        }

        public RemoteCertificateValidationCallback TransportValidator { get; } = (_, _, _, _) => false;

        public ApplicationName? TrustApplication { get; private set; }

        RemoteCertificateValidationCallback? IResourceTransportTrustProvider.CreateOutboundTrustValidator(ApplicationName application)
        {
            TrustApplication = application;
            return TransportValidator;
        }

        public override ResourceName Name => "control-plane-test";

        protected override IReadOnlyList<IApplicationResourceController> Controllers => _controllers;

        protected override IApplicationResourceStateManager State => _state;

        protected override Task<IResourceArtifact> GatherAsync(
            IApplicationResource resource,
            CancellationToken cancellationToken) =>
            Task.FromResult<IResourceArtifact>(new TestArtifact(resource.Id));
    }

    private sealed class TestController : IApplicationResourceController
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
            ResourceManifest manifest = FindManifest(context);
            ResourceManifestEndpoint endpoint = manifest.Endpoints[0];
            context.State.SetState(
                context.Resource.Id,
                ResourceLifecycle.Running,
                observedEndpoints:
                [
                    new ResourceEndpoint(
                        endpoint.Name,
                        endpoint.Scheme,
                        endpoint.ContainerPort,
                        Host: "127.0.0.1"),
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
            CancellationToken cancellationToken = default) =>
            StopAsync(context, cancellationToken);

        private static ResourceManifest FindManifest(IResourceControlContext context)
        {
            for (int index = 0; index < context.Model.Manifests.Count; index++)
            {
                if (context.Model.Manifests[index].Name == context.Resource.Name)
                {
                    return context.Model.Manifests[index];
                }
            }

            throw new InvalidOperationException($"Resource '{context.Resource.Name}' has no manifest.");
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

    private sealed class RecordingCommandDispatcher : IResourceCommandDispatcher
    {
        public string ResourceKind => "test";

        public int Applied { get; private set; }

        public int Deleted { get; private set; }

        public ValueTask<ReadOnlyMemory<byte>> ApplyAsync(
            Uri address,
            string bearerToken,
            ResourceCommand command,
            RemoteCertificateValidationCallback? serverCertificateValidator,
            CancellationToken cancellationToken = default)
        {
            address.AbsolutePath.ShouldBe("/cohesion/v1");
            serverCertificateValidator.ShouldBeNull();
            JsonWebToken.Parse(bearerToken).Audiences.ShouldContain("api");
            Applied++;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>("accepted"u8.ToArray());
        }

        public ValueTask DeleteAsync(
            Uri address,
            string bearerToken,
            ResourceCommand command,
            RemoteCertificateValidationCallback? serverCertificateValidator,
            CancellationToken cancellationToken = default)
        {
            address.AbsolutePath.ShouldBe("/cohesion/v1");
            serverCertificateValidator.ShouldBeNull();
            JsonWebToken.Parse(bearerToken).Audiences.ShouldContain("api");
            Deleted++;
            return ValueTask.CompletedTask;
        }
    }
}
