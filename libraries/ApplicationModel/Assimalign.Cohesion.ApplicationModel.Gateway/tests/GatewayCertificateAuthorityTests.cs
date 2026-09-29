using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// Source-free endpoint certificates (owner decision 3): a registered certificate authority
/// issues them; without one the gateway development authority issues them only in Local, and
/// every other environment fails loudly. The authority resource's own leaf always comes from
/// the gateway authority.
/// </summary>
public sealed class GatewayCertificateAuthorityTests
{
    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Certificate authority: Unbound outside Local fails loudly naming the endpoint and the fix")]
    [InlineData(AppEnvironment.Keys.Development)]
    [InlineData(AppEnvironment.Keys.Production)]
    public async Task StartAsync_UnboundAuthorityOutsideLocal_ShouldFailNamingEndpoint(string environment)
    {
        // Arrange
        string root = CreateDirectory();
        var controller = new CertificateController();
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [controller], options: new ApplicationGatewayOptions { ExportDirectory = root });
        IApplicationBuilder builder = Application.CreateBuilder("appa", ["--environment", environment]).UseGateway(gateway);
        builder.AddResource(Manifest("web", "Web"));
        IApplicationModel model = builder.Build().Model;

        try
        {
            // Act
            InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(
                () => ((IApplicationGateway)gateway).StartAsync(model));

            // Assert
            failure.Message.ShouldContain("Endpoint 'https' on resource 'web'", Case.Sensitive);
            failure.Message.ShouldContain("no certificate authority is registered", Case.Sensitive);
            failure.Message.ShouldContain("only in Local", Case.Sensitive);
            failure.Message.ShouldContain("builder.Providers.CertificateAuthority", Case.Sensitive);
            failure.Message.ShouldContain(".AsCertificateAuthority()", Case.Sensitive);
            controller.Inputs.ShouldNotContainKey("web");
            File.Exists(Path.Combine(root, "appa", ".state", "certs", "web-https.pem.protected")).ShouldBeFalse();
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Certificate authority: Unbound in Local issues the development leaf")]
    public async Task StartAsync_UnboundAuthorityInLocal_ShouldIssueDevelopmentLeaf()
    {
        // Arrange
        string root = CreateDirectory();
        var controller = new CertificateController();
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [controller], options: new ApplicationGatewayOptions { ExportDirectory = root });
        IApplicationBuilder builder = Application.CreateBuilder("appa", ["--environment", AppEnvironment.Keys.Local]).UseGateway(gateway);
        builder.AddResource(Manifest("web", "Web"));

        try
        {
            // Act
            await ((IApplicationGateway)gateway).StartAsync(builder.Build().Model);

            // Assert
            IssuedByDevelopmentRoot(root, controller.Inputs["web"]).ShouldBeTrue();
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Certificate authority: The authority's own leaf comes from the gateway outside Local")]
    public async Task StartAsync_AuthorityOwnLeafOutsideLocal_ShouldComeFromGatewayAuthority()
    {
        // Arrange
        string root = CreateDirectory();
        var fixture = new GatewayCertificateAuthority(Path.Combine(root, "fixture"), "fixture");
        string leaf = fixture.Issue("web-https", []);
        string anchors = Encoding.UTF8.GetString(fixture.ExportAnchors().Span);
        var authority = new RecordingCertificateAuthority(new ResourceCertificate(leaf, anchors), "KeyStore");
        var controller = new CertificateController();
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [controller], options: new ApplicationGatewayOptions { ExportDirectory = root });
        IApplicationBuilder builder = Application.CreateBuilder("appa", ["--environment", AppEnvironment.Keys.Production]).UseGateway(gateway);
        // Declared first and independent of the authority: the gateway orders the authority's
        // resource ahead of it so the authority is Running when the Web leaf is requested.
        builder.AddResource(Manifest("web", "Web"));
        builder.AddResource(Manifest("ca", "KeyStore"));
        builder.Providers.CertificateAuthority = new ResourceProviderBinding<IResourceCertificateAuthority>("ca", authority);

        try
        {
            // Act
            await ((IApplicationGateway)gateway).StartAsync(builder.Build().Model);

            // Assert
            IssuedByDevelopmentRoot(root, controller.Inputs["ca"]).ShouldBeTrue();
            Encoding.UTF8.GetString(controller.Inputs["web"].Mounts["tls"].Content.Span).ShouldBe(leaf);
            Encoding.UTF8.GetString(controller.Inputs["web"].TrustBundle.Span).ShouldContain(anchors.Trim(), Case.Sensitive);
            (ResourceCertificateRequest request, ResourceProviderConnection? connection) = authority.Requests.ShouldHaveSingleItem();
            request.Application.ShouldBe(ApplicationName.Parse("appa"));
            request.Resource.ShouldBe((ResourceName)"web");
            request.Endpoint.ShouldBe("https");
            request.LeafName.ShouldBe("web-https");
            ResourceProviderConnection authorityConnection = connection.ShouldNotBeNull();
            authorityConnection.Resource.ShouldBe((ResourceName)"ca");
            authorityConnection.ResourceKind.ShouldBe("KeyStore");
            authorityConnection.ControlPlaneAddress.ShouldBe(new Uri("https://127.0.0.1:7443/cohesion/v1"));
            authorityConnection.ServerCertificateValidator.ShouldNotBeNull();
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Certificate authority: A separate trust-store resource is ordered after the authority and receives its leaf outside Local")]
    public async Task StartAsync_SeparateTrustStoreOutsideLocal_ShouldFollowAuthorityAndReceiveItsLeaf()
    {
        // Arrange
        string root = CreateDirectory();
        var fixture = new GatewayCertificateAuthority(Path.Combine(root, "fixture"), "fixture");
        string leaf = fixture.Issue("issued", []);
        string anchors = Encoding.UTF8.GetString(fixture.ExportAnchors().Span);
        var authority = new RecordingCertificateAuthority(new ResourceCertificate(leaf, anchors), "KeyStore");
        var trustStore = new RecordingTrustedIssuerStore(resourceKind: "KeyStore");
        var controller = new CertificateController();
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [controller], options: new ApplicationGatewayOptions { ExportDirectory = root });
        IApplicationBuilder builder = Application.CreateBuilder("appa", ["--environment", AppEnvironment.Keys.Production]).UseGateway(gateway);
        // Declaration order puts the trust store first; neither store depends on the other.
        builder.AddResource(Manifest("trust", "KeyStore"));
        builder.AddResource(Manifest("web", "Web"));
        builder.AddResource(Manifest("ca", "KeyStore"));
        builder.Providers.TrustStore = new ResourceProviderBinding<ITrustedIssuerStore>("trust", trustStore);
        builder.Providers.CertificateAuthority = new ResourceProviderBinding<IResourceCertificateAuthority>("ca", authority);

        try
        {
            // Act
            await ((IApplicationGateway)gateway).StartAsync(builder.Build().Model);

            // Assert
            controller.Order.ShouldBe(["ca", "trust", "web"]);
            IssuedByDevelopmentRoot(root, controller.Inputs["ca"]).ShouldBeTrue();
            Encoding.UTF8.GetString(controller.Inputs["trust"].Mounts["tls"].Content.Span).ShouldBe(leaf);
            Encoding.UTF8.GetString(controller.Inputs["web"].Mounts["tls"].Content.Span).ShouldBe(leaf);
            authority.Requests.Select(entry => entry.Request.LeafName).ShouldBe(["trust-https", "web-https"]);
            trustStore.Reads.ShouldNotBeEmpty();
            trustStore.Reads.ShouldAllBe(connection => connection != null && connection.Resource == (ResourceName)"trust");
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Certificate authority: A failing authority outside Local never downgrades to the development leaf")]
    public async Task StartAsync_BoundAuthorityFailureOutsideLocal_ShouldFailLoudly()
    {
        // Arrange
        string root = CreateDirectory();
        var authority = new RecordingCertificateAuthority(resourceKind: "KeyStore")
        {
            Failure = new HttpRequestException("authority offline"),
        };
        var controller = new CertificateController();
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [controller], options: new ApplicationGatewayOptions { ExportDirectory = root });
        IApplicationBuilder builder = Application.CreateBuilder("appa", ["--environment", AppEnvironment.Keys.Production]).UseGateway(gateway);
        builder.AddResource(Manifest("ca", "KeyStore"));
        builder.AddResource(Manifest("web", "Web"));
        builder.Providers.CertificateAuthority = new ResourceProviderBinding<IResourceCertificateAuthority>("ca", authority);
        IApplicationModel model = builder.Build().Model;

        try
        {
            // Act
            InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(
                () => ((IApplicationGateway)gateway).StartAsync(model));

            // Assert
            failure.Message.ShouldContain("Endpoint 'https' on resource 'web'", Case.Sensitive);
            failure.Message.ShouldContain("certificate authority 'ca'", Case.Sensitive);
            failure.Message.ShouldContain("authority offline", Case.Sensitive);
            failure.Message.ShouldContain("never falls back", Case.Sensitive);
            File.Exists(Path.Combine(root, "appa", ".state", "certs", "web-https.pem.protected")).ShouldBeFalse();
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Certificate authority: A failing authority in Local bootstraps with the development leaf")]
    public async Task StartAsync_BoundAuthorityFailureInLocal_ShouldFallBackToDevelopmentLeaf()
    {
        // Arrange
        string root = CreateDirectory();
        var authority = new RecordingCertificateAuthority(resourceKind: "KeyStore")
        {
            Failure = new HttpRequestException("authority not enrolled"),
        };
        var controller = new CertificateController();
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [controller], options: new ApplicationGatewayOptions { ExportDirectory = root });
        IApplicationBuilder builder = Application.CreateBuilder("appa", ["--environment", AppEnvironment.Keys.Local]).UseGateway(gateway);
        builder.AddResource(Manifest("ca", "KeyStore"));
        builder.AddResource(Manifest("web", "Web"));
        builder.Providers.CertificateAuthority = new ResourceProviderBinding<IResourceCertificateAuthority>("ca", authority);

        try
        {
            // Act
            await ((IApplicationGateway)gateway).StartAsync(builder.Build().Model);

            // Assert
            authority.Requests.ShouldHaveSingleItem().Request.LeafName.ShouldBe("web-https");
            IssuedByDevelopmentRoot(root, controller.Inputs["web"]).ShouldBeTrue();
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool IssuedByDevelopmentRoot(string root, ResourceInputs inputs)
    {
        string pem = Encoding.UTF8.GetString(inputs.Mounts["tls"].Content.Span);
        using X509Certificate2 leaf = X509Certificate2.CreateFromPem(pem, pem);
        using X509Certificate2 developmentRoot = X509Certificate2.CreateFromPem(
            File.ReadAllText(Path.Combine(root, "appa", ".state", "certs", "root.crt")));
        return leaf.IssuerName.RawData.AsSpan().SequenceEqual(developmentRoot.SubjectName.RawData);
    }

    private static string CreateDirectory() =>
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "ca-" + Guid.NewGuid().ToString("N"))).FullName;

    private static ResourceManifest Manifest(string name, string kind) => new()
    {
        Name = name,
        Application = "appa",
        Kind = kind,
        ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
        Artifact = new ResourceManifestArtifact { Assembly = "test.dll", AppHost = "test" },
        Endpoints = [new ResourceManifestEndpoint { Name = "https", Scheme = "https", Protocol = "tcp", ContainerPort = 8443, Certificate = "tls" }],
        Mounts = [new ResourceManifestMount { Name = "tls", Kind = ResourceMountKind.Secret, ContainerPath = "/cohesion/mounts/tls" }],
        ControlPlane = new ResourceManifestControlPlane { Endpoint = "https", Path = "/cohesion/v1" },
        Lifecycle = new ResourceManifestLifecycle { Workload = WorkloadKind.Deployment, Replicas = 1, RestartPolicy = "Never" },
    };

    private sealed class CertificateController : IApplicationResourceController
    {
        public Dictionary<string, ResourceInputs> Inputs { get; } = new(StringComparer.Ordinal);

        public List<string> Order { get; } = new();

        public bool CanRealize(ResourcePlan plan, out string? reason)
        {
            reason = null;
            return true;
        }

        public Task ReconcileAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = context.Resource.Name.ToString();
            Inputs[name] = context.Inputs;
            Order.Add(name);
            int port = name == "ca" ? 7443 : 8443;
            context.State.SetState(
                context.Resource.Id,
                ResourceLifecycle.Running,
                observedEndpoints: [new ResourceEndpoint("https", "https", port, Host: "127.0.0.1")]);
            return Task.CompletedTask;
        }

        public Task StopAsync(IResourceControlContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(IResourceControlContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
