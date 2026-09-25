using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;
using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Hosting.Resources;
using HostingMount = Assimalign.Cohesion.Hosting.Resources.ResourceMount;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

[Collection(LocalGatewayConsoleCollection.Name)]
public sealed class GatewayCertificateTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Development issuer: Persists keys, leaf SANs and certificate-only trust")]
    public void Issue_Restart_ShouldPreserveIdentityAndAnchors()
    {
        string root = CreateDirectory();
        try
        {
            string application = Path.Combine(root, "cert-tests");
            var authority = new GatewayCertificateAuthority(application, "cert-tests");
            string pem = authority.Issue("web-https", ["web.example.test"]);
            using X509Certificate2 leaf = X509Certificate2.CreateFromPem(pem, pem);
            X509SubjectAlternativeNameExtension san = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
            san.EnumerateDnsNames().ShouldContain("web.example.test");
            san.EnumerateDnsNames().ShouldContain("localhost");
            san.EnumerateIPAddresses().ShouldContain(IPAddress.Loopback);
            san.EnumerateIPAddresses().ShouldContain(IPAddress.IPv6Loopback);
            File.Exists(Path.Combine(application, ".state", "certs", "root.crt")).ShouldBeTrue();
            File.Exists(Path.Combine(application, ".state", "certs", "root.key.protected")).ShouldBeTrue();
            string anchors = Encoding.UTF8.GetString(authority.ExportAnchors().Span);
            anchors.ShouldContain("BEGIN CERTIFICATE");
            anchors.ShouldNotContain("PRIVATE KEY");
            var restarted = new GatewayCertificateAuthority(application, "cert-tests");
            restarted.Issue("web-https", ["web.example.test"]).ShouldBe(pem);
            Encoding.UTF8.GetString(restarted.ExportAnchors().Span).ShouldBe(anchors);
            pem.IndexOf("BEGIN CERTIFICATE", StringComparison.Ordinal).ShouldBeLessThan(pem.IndexOf("BEGIN PRIVATE KEY", StringComparison.Ordinal));
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(Path.Combine(application, ".state", "certs", "root.key.protected")).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - HTTPS probe: Trusts only the application's issuer")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Probe_ApplicationTrust_ShouldAuthenticateIssuer(bool trusted)
    {
        string root = CreateDirectory();
        try
        {
            var authority = new GatewayCertificateAuthority(Path.Combine(root, "app"), "cert-tests");
            string own = authority.Issue("web-https", []);
            string pem = trusted ? own : new GatewayCertificateAuthority(Path.Combine(root, "unrelated"), "unrelated").Issue("web-https", []);
            var context = new ResourceContext(mounts: new Dictionary<string, HostingMount> { ["tls"] = HostingMount.FromBytes(Encoding.UTF8.GetBytes(pem)) });
            context.TryGetEndpointCertificate("https", out X509Certificate2? leaf).ShouldBeTrue();
            using (leaf)
            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                listener.Start();
                Task serve = ServeAsync(listener, leaf, timeout.Token);
                var runner = new LocalProbeRunner(new LocalGatewayOptions { ProbeTimeout = TimeSpan.FromSeconds(5) });
                var configuration = new LocalResourceConfiguration("cert-tests", new InMemoryResourceStateManager(), null!, null!,
                    new Dictionary<string, string> { [ResourceEnvironment.TrustBundlePath] = authority.TrustPath }, [],
                    null, null, null, null, false, RestartPolicy.Never, TimeSpan.FromSeconds(1), false);
                ProbeAttemptResult result = await runner.RunAsync(ProbeSpec.Http(new Uri($"https://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}/")), configuration, timeout.Token);
                result.Succeeded.ShouldBe(trusted);
                await serve;
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local HTTPS: Real Web host starts with the selected certificate issuer")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalGateway_RealWebHttps_ShouldUseExpectedIssuer(bool withStore)
    {
        string root = CreateDirectory();
        var gateway = new LocalGateway(new LocalGatewayOptions
        {
            StateDirectory = root,
            BaseDirectory = AppContext.BaseDirectory,
            ReadinessBudget = TimeSpan.FromSeconds(25),
            ProbeInterval = TimeSpan.FromMilliseconds(100),
            ProbeTimeout = TimeSpan.FromSeconds(3),
            StopGrace = TimeSpan.FromSeconds(3),
        });
        var details = new ConcurrentQueue<string>();
        gateway.ResourceStates.StateChanged += (_, change) => details.Enqueue(change.Detail ?? change.Current.ToString());
        IApplicationBuilder builder = Application.CreateBuilder("cert-tests", ["--environment", "Development"]).UseGateway(gateway);
        IApplicationResourceDescriptor? store = null;
        if (withStore)
        {
            store = builder.AddResource(Manifest("secrets", "SecretStore", "api", "Assimalign.Cohesion.ApplicationModel.Gateway.TestHost") with
            {
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["TEST_RESOURCE_AREA"] = "SecretStore",
                    ["TEST_COMMAND_DATA"] = Path.Combine(root, "store-data"),
                },
            });
        }
        IApplicationResourceDescriptor web = builder.AddResource(Manifest("web", "Web", "https", "Assimalign.Cohesion.Web.HttpsHost"));
        if (store is not null)
        {
            web.DependsOn(store);
        }
        IApplication application = builder.Build();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Task run = application.RunAsync(cancellation.Token);
        try
        {
            ResourceLifecycle reached = await gateway.ResourceStates.WaitForStateAsync(web.Resource.Id,
                new HashSet<ResourceLifecycle> { ResourceLifecycle.Running, ResourceLifecycle.Failed, ResourceLifecycle.Stopped }, TimeSpan.FromSeconds(35));
            reached.ShouldBe(ResourceLifecycle.Running, string.Join("\n", details));
            string directory = Path.Combine(root, "cert-tests");
            byte[] mounted = new HostingMount(Path.Combine(directory, "web", "tls")).ReadAllBytes();
            string pem = Encoding.UTF8.GetString(mounted);
            using X509Certificate2 leaf = X509Certificate2.CreateFromPem(pem, pem);
            using X509Certificate2 developmentRoot = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(directory, ".state", "certs", "root.crt")));
            bool gatewayIssued = leaf.IssuerName.RawData.AsSpan().SequenceEqual(developmentRoot.SubjectName.RawData);
            gatewayIssued.ShouldBe(!withStore);
            File.Exists(Path.Combine(directory, ".state", "certs", "web-https.pem.protected")).ShouldBe(!withStore);
            byte[] trust = new HostingMount(Path.Combine(directory, "web", ".state", "trust.pem")).ReadAllBytes();
            Encoding.UTF8.GetString(trust).ShouldNotContain("PRIVATE KEY");
            var observed = gateway.ResourceStates.GetObservedEndpoints(web.Resource.Id);
            observed.Single().Scheme.ShouldBe("https");
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (OperationCanceledException)
            {
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(string.Join("\n", details), exception);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private static ResourceManifest Manifest(string name, string kind, string endpoint, string assembly) => new()
    {
        Name = name,
        Application = "cert-tests",
        Kind = kind,
        ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
        Artifact = new ResourceManifestArtifact { Assembly = assembly + ".dll", AppHost = Path.Combine(AppContext.BaseDirectory, assembly + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)) },
        Endpoints = [new ResourceManifestEndpoint { Name = endpoint, Scheme = "https", Protocol = "tcp", ContainerPort = 8443, Certificate = "tls" }],
        Mounts = [new ResourceManifestMount { Name = "tls", Kind = ResourceMountKind.Secret, ContainerPath = "/cohesion/mounts/tls" }],
        ControlPlane = new ResourceManifestControlPlane { Endpoint = endpoint, Path = "/cohesion/v1" },
        Lifecycle = new ResourceManifestLifecycle { Workload = WorkloadKind.Deployment, Replicas = 1, RestartPolicy = "Never", StopGraceSeconds = 3 },
    };

    private static string CreateDirectory() => Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "cert-" + Guid.NewGuid().ToString("N"))).FullName;

    private static async Task ServeAsync(TcpListener listener, X509Certificate2 leaf, CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
        using var ssl = new SslStream(client.GetStream());
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = leaf }, cancellationToken);
            byte[] request = new byte[4096];
            if (await ssl.ReadAsync(request, cancellationToken) > 0)
            {
                await ssl.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), cancellationToken);
            }
        }
        catch (Exception exception) when (exception is AuthenticationException or IOException)
        {
            // The negative case intentionally rejects the server during its handshake.
        }
    }
}
