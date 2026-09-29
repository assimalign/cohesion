using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;
using Assimalign.Cohesion.Hosting.Resources;
using HostingMount = Assimalign.Cohesion.Hosting.Resources.ResourceMount;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

[Collection(LocalGatewayConsoleCollection.Name)]
public sealed class GatewayTelemetryTests
{
    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Telemetry resolution waits for a running registered sink without adding dependencies")]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveTelemetry_BeforeSinkIsRunning_ShouldInjectNothing(bool registerSink)
    {
        // Arrange
        var gateway = new LocalGateway();
        IApplicationBuilder builder = Application.CreateBuilder("telemetry-tests", ["--environment", AppEnvironment.Keys.Local]).UseGateway(gateway);
        IApplicationResourceDescriptor sink = builder.AddResource(Manifest("logs", "LogSpace", "query", "Assimalign.Cohesion.LogSpace.SinkHost") with { Endpoints = [Endpoint("query", 8443), Endpoint("otlp", 4318)] });
        builder.AddResource(Manifest("web", "Web", "https", "Assimalign.Cohesion.Web.HttpsHost"));
        if (registerSink) { builder.Providers.Telemetry = ResourceTelemetrySink.FromResource(sink); }
        IApplicationModel model = builder.Build().Model;
        model.Descriptors.Single(descriptor => descriptor.Resource.Name.ToString() == "web").Dependencies.ShouldBeEmpty();
        InitializeSession(gateway, model);

        // Act
        object? injection = ResolveTelemetry(gateway, model, "web");

        // Assert
        injection.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Telemetry: A running sink is used only when registered, never discovered by kind")]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveTelemetry_RunningSink_ShouldRequireRegistration(bool registerSink)
    {
        // Arrange
        string root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "tr-" + Guid.NewGuid().ToString("N"))).FullName;
        var gateway = new LocalGateway(new LocalGatewayOptions { StateDirectory = root });
        IApplicationBuilder builder = Application.CreateBuilder("telemetry-tests", ["--environment", AppEnvironment.Keys.Local]).UseGateway(gateway);
        IApplicationResourceDescriptor sink = builder.AddResource(Manifest("logs", "LogSpace", "query", "Assimalign.Cohesion.LogSpace.SinkHost") with { Endpoints = [Endpoint("query", 8443), Endpoint("otlp", 4318)] });
        builder.AddResource(Manifest("web", "Web", "https", "Assimalign.Cohesion.Web.HttpsHost"));
        if (registerSink) { builder.Providers.Telemetry = ResourceTelemetrySink.FromResource(sink); }
        IApplicationModel model = builder.Build().Model;
        InitializeSession(gateway, model);
        IApplicationResource logs = model.Resources.Single(resource => resource.Name.ToString() == "logs");
        gateway.ResourceStates.SetState(logs.Id, ResourceLifecycle.Running,
            observedEndpoints: [new ResourceEndpoint("otlp", "https", 4318, Host: "127.0.0.1")]);

        try
        {
            InitializeTrust(gateway, model);

            // Act
            var injection = (ResourceTelemetryInjection?)ResolveTelemetry(gateway, model, "web");

            // Assert
            if (!registerSink)
            {
                injection.ShouldBeNull();
                return;
            }

            injection.ShouldNotBeNull().Endpoint.ShouldBe(new Uri("https://127.0.0.1:4318"));
            string header = Encoding.UTF8.GetString(injection.HeadersDocument.Span);
            header.StartsWith("Authorization: Bearer ", StringComparison.Ordinal).ShouldBeTrue();
            using JsonDocument claims = JsonDocument.Parse(Base64Url.DecodeFromChars(header["Authorization: Bearer ".Length..].Trim().Split('.')[1]));
            claims.RootElement.GetProperty("aud").ToString().ShouldContain("logs", Case.Sensitive);
            claims.RootElement.GetProperty("sub").GetString().ShouldBe("web");
            claims.RootElement.GetProperty("scope").GetString().ShouldBe("telemetry");
            ((ResourceTelemetryInjection?)ResolveTelemetry(gateway, model, "logs")).ShouldBeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Telemetry: A registered sink reachable only over plaintext HTTP receives no emitter credential, even in Local")]
    public void ResolveTelemetry_RunningSinkOverPlaintextLoopback_ShouldInjectNothing()
    {
        // Arrange
        string root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "tr-" + Guid.NewGuid().ToString("N"))).FullName;
        var gateway = new LocalGateway(new LocalGatewayOptions { StateDirectory = root });
        IApplicationBuilder builder = Application.CreateBuilder("telemetry-tests", ["--environment", AppEnvironment.Keys.Local]).UseGateway(gateway);
        IApplicationResourceDescriptor sink = builder.AddResource(Manifest("logs", "LogSpace", "query", "Assimalign.Cohesion.LogSpace.SinkHost") with { Endpoints = [Endpoint("query", 8443), Endpoint("otlp", 4318)] });
        builder.AddResource(Manifest("web", "Web", "https", "Assimalign.Cohesion.Web.HttpsHost"));
        builder.Providers.Telemetry = ResourceTelemetrySink.FromResource(sink);
        IApplicationModel model = builder.Build().Model;
        InitializeSession(gateway, model);
        IApplicationResource logs = model.Resources.Single(resource => resource.Name.ToString() == "logs");
        gateway.ResourceStates.SetState(logs.Id, ResourceLifecycle.Running,
            observedEndpoints: [new ResourceEndpoint("otlp", "http", 4318, Host: "127.0.0.1")]);

        try
        {
            InitializeTrust(gateway, model);

            // Act
            var injection = (ResourceTelemetryInjection?)ResolveTelemetry(gateway, model, "web");

            // Assert: the emitter credential never travels in plaintext, as with kind discovery.
            injection.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Telemetry: An external sink injects its address and the named headers parameter")]
    public void ResolveTelemetry_ExternalSink_ShouldInjectAddressAndHeadersParameter()
    {
        // Arrange
        var options = new LocalGatewayOptions();
        options.Parameters["otlp-headers"] = "Authorization: Basic dGVzdA==\n";
        var gateway = new LocalGateway(options);
        IApplicationBuilder builder = Application.CreateBuilder("telemetry-tests", ["--environment", AppEnvironment.Keys.Production]).UseGateway(gateway);
        builder.AddResource(Manifest("web", "Web", "https", "Assimalign.Cohesion.Web.HttpsHost"));
        builder.Providers.Telemetry = ResourceTelemetrySink.External(new Uri("https://collector.example.test:4318"), "otlp-headers");
        IApplicationModel model = builder.Build().Model;

        // Act
        var injection = (ResourceTelemetryInjection?)ResolveTelemetry(gateway, model, "web");

        // Assert
        injection.ShouldNotBeNull().Endpoint.ShouldBe(new Uri("https://collector.example.test:4318"));
        Encoding.UTF8.GetString(injection.HeadersDocument.Span).ShouldBe("Authorization: Basic dGVzdA==\n");
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Telemetry: An external sink refuses an unbound headers parameter or a plaintext address")]
    [InlineData("https://collector.example.test:4318", false, "is not bound")]
    [InlineData("http://collector.example.test:4318", true, "non-TLS")]
    public void ResolveTelemetry_ExternalSinkMisconfigured_ShouldFailLoudly(string address, bool bindParameter, string expected)
    {
        // Arrange
        var options = new LocalGatewayOptions();
        if (bindParameter) { options.Parameters["otlp-headers"] = "Authorization: Basic dGVzdA==\n"; }
        var gateway = new LocalGateway(options);
        IApplicationBuilder builder = Application.CreateBuilder("telemetry-tests", ["--environment", AppEnvironment.Keys.Production]).UseGateway(gateway);
        builder.AddResource(Manifest("web", "Web", "https", "Assimalign.Cohesion.Web.HttpsHost"));
        builder.Providers.Telemetry = ResourceTelemetrySink.External(new Uri(address), "otlp-headers");
        IApplicationModel model = builder.Build().Model;

        // Act
        InvalidOperationException failure = Should.Throw<InvalidOperationException>(() => ResolveTelemetry(gateway, model, "web"));

        // Assert
        failure.Message.ShouldContain(expected, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Telemetry: Real Web exports through trusted HTTPS to mounted LogSpace")]
    public async Task LocalGateway_WebToLogSpace_ShouldPersistAndEnforceTelemetryScope()
    {
        string root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "ot-" + Guid.NewGuid().ToString("N"))).FullName;
        var gateway = new LocalGateway(new LocalGatewayOptions { StateDirectory = root, BaseDirectory = AppContext.BaseDirectory,
            ReadinessBudget = TimeSpan.FromSeconds(30), ProbeInterval = TimeSpan.FromMilliseconds(100), ProbeTimeout = TimeSpan.FromSeconds(3), StopGrace = TimeSpan.FromSeconds(5) });
        var details = new ConcurrentQueue<string>();
        gateway.ResourceStates.StateChanged += (_, change) => details.Enqueue(change.Detail ?? change.Current.ToString());
        IApplicationBuilder builder = Application.CreateBuilder("telemetry-tests", ["--environment", AppEnvironment.Keys.Local]).UseGateway(gateway);
        IApplicationResourceDescriptor sink = builder.AddResource(Manifest("logs", "LogSpace", "query", "Assimalign.Cohesion.LogSpace.SinkHost") with
        {
            Endpoints = [Endpoint("query", 8443), Endpoint("otlp", 4318)],
            Lifecycle = new ResourceManifestLifecycle { Workload = WorkloadKind.StatefulSet, Replicas = 1, RestartPolicy = "Never", StopGraceSeconds = 5 },
            Mounts = [new ResourceManifestMount { Name = "tls", Kind = ResourceMountKind.Secret, ContainerPath = "/cohesion/mounts/tls" },
                new ResourceManifestMount { Name = "data", Kind = ResourceMountKind.Volume, ContainerPath = "/data", Size = "10Gi" }],
        });
        IApplicationResourceDescriptor web = builder.AddResource(Manifest("web", "Web", "https", "Assimalign.Cohesion.Web.HttpsHost"));
        web.DependsOn(sink);
        builder.Providers.Telemetry = ResourceTelemetrySink.FromResource(sink);
        IApplication application = builder.Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        Task run = application.RunAsync(timeout.Token);
        bool acceptedStartup = false;
        try
        {
            var reached = await gateway.ResourceStates.WaitForStateAsync(web.Resource.Id,
                new HashSet<ResourceLifecycle> { ResourceLifecycle.Running, ResourceLifecycle.Failed, ResourceLifecycle.Stopped }, TimeSpan.FromSeconds(45));
            reached.ShouldBe(ResourceLifecycle.Running, string.Join("\n", details));
            string directory = Path.Combine(root, "telemetry-tests");
            string header = Read(Path.Combine(directory, "web", ".state", "telemetry.headers"));
            header.StartsWith("Authorization: Bearer ", StringComparison.Ordinal).ShouldBeTrue();
            header.Count(character => character == '\n').ShouldBe(1);
            string telemetryToken = header["Authorization: Bearer ".Length..].Trim();
            string bootstrap = Read(Path.Combine(directory, "logs", ".state", "bootstrap.token"));
            string.Equals(telemetryToken, bootstrap, StringComparison.Ordinal).ShouldBeFalse();
            using JsonDocument claims = JsonDocument.Parse(Base64Url.DecodeFromChars(telemetryToken.Split('.')[1]));
            claims.RootElement.GetProperty("aud").ToString().ShouldContain("logs", Case.Sensitive);
            claims.RootElement.GetProperty("sub").GetString().ShouldBe("web");
            claims.RootElement.GetProperty("scope").GetString().ShouldBe("telemetry");
            File.Exists(Path.Combine(directory, "logs", ".state", "telemetry.headers")).ShouldBeFalse();

            var context = new ResourceContext(applicationName: "telemetry-tests", resourceName: "web", environmentName: AppEnvironment.Keys.Local, gatewayName: "local",
                contentRootPath: null, endpoints: null, mounts: null, settings: null, references: null,
                bootstrapCredential: ReadOnlyMemory<byte>.Empty, applicationTrustKey: ReadOnlyMemory<byte>.Empty,
                ambientValues: new Dictionary<string, string?> { [AppEnvironment.Variables.TrustBundlePath] = Path.Combine(directory, "web", ".state", "trust.pem") });
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
            handler.SslOptions.RemoteCertificateValidationCallback = context.CreateOutboundTrustValidator();
            using var client = new HttpClient(handler);
            var observed = gateway.ResourceStates.GetObservedEndpoints(sink.Resource.Id);
            Uri query = Address(observed.Single(endpoint => endpoint.Name == "query"));
            Uri ingest = Address(observed.Single(endpoint => endpoint.Name == "otlp"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bootstrap);
            string records = string.Empty;
            for (int attempt = 0; attempt < 100; attempt++)
            {
                using var response = await client.GetAsync(new Uri(query, "/cohesion/v1/logs?resource=web&since=2020-01-01T00:00:00Z"), timeout.Token);
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                records = await response.Content.ReadAsStringAsync(timeout.Token);
                if (records.Contains("Resource telemetry started", StringComparison.Ordinal)) { break; }
                await Task.Delay(100, timeout.Token);
            }
            records.ShouldContain("Resource telemetry started", Case.Sensitive);
            acceptedStartup = true;
            Directory.EnumerateFiles(Path.Combine(directory, "logs"), "logs-*.ndjson", SearchOption.AllDirectories).ShouldNotBeEmpty();

            client.DefaultRequestHeaders.Authorization = null;
            using (var absent = await client.PostAsync(new Uri(ingest, "/v1/logs"), Json("{}"), timeout.Token))
            { absent.StatusCode.ShouldBe(HttpStatusCode.Unauthorized); absent.Headers.WwwAuthenticate.Single().Scheme.ShouldBe("Bearer"); }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Read(Path.Combine(directory, "web", ".state", "bootstrap.token")));
            using (var wrong = await client.PostAsync(new Uri(ingest, "/v1/logs"), Json("{}"), timeout.Token)) { wrong.StatusCode.ShouldBe(HttpStatusCode.Forbidden); }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", telemetryToken);
            foreach (string route in new[] { "/cohesion/v1/stop", "/cohesion/v1/commands" })
            { using var refused = await client.PostAsync(new Uri(query, route), Json("{}"), timeout.Token); refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden); }
            using (var refusedQuery = await client.GetAsync(new Uri(query, "/cohesion/v1/logs"), timeout.Token)) { refusedQuery.StatusCode.ShouldBe(HttpStatusCode.Forbidden); }
            using (var bad = await client.PostAsync(new Uri(ingest, "/v1/logs"), Json("{"), timeout.Token)) { bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest); }
            using (var binary = await client.PostAsync(new Uri(ingest, "/v1/logs"), new StringContent("", Encoding.UTF8, "application/x-protobuf"), timeout.Token)) { binary.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType); }
            using (var oversized = await client.PostAsync(new Uri(ingest, "/v1/logs"), Json(new string('x', 1024 * 1024 + 1)), timeout.Token)) { ((int)oversized.StatusCode).ShouldBe(413); }
            using (var oversizedChunked = await client.PostAsync(new Uri(ingest, "/v1/logs"), new OversizedChunkedContent(), timeout.Token)) { ((int)oversizedChunked.StatusCode).ShouldBe(413); }
            using (var unknown = await client.GetAsync(new Uri(ingest, "/unknown"), timeout.Token)) { unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound); }
            using (var method = await client.GetAsync(new Uri(ingest, "/v1/logs"), timeout.Token)) { method.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed); method.Content.Headers.Allow.ShouldContain("POST"); }
            string payload = """{"resourceLogs":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"web"}}]},"scopeLogs":[{"scope":{"name":"manual"},"logRecords":[{"timeUnixNano":"1700000000000000000","severityNumber":9,"body":{"stringValue":"first"}},{"timeUnixNano":"1700000001000000000","severityNumber":9,"body":{"stringValue":"second"}}]}]}]}""";
            using (var accepted = await client.PostAsync(new Uri(ingest, "/v1/logs"), Json(payload), timeout.Token))
            { accepted.StatusCode.ShouldBe(HttpStatusCode.OK); (await accepted.Content.ReadAsStringAsync(timeout.Token)).ShouldBe("{\"partialSuccess\":{}}"); }
            await Task.Delay(500, timeout.Token);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bootstrap);
            using var first = await client.GetAsync(new Uri(query, "/cohesion/v1/logs?resource=web&limit=1"), timeout.Token);
            first.Content.Headers.ContentType!.MediaType.ShouldBe("application/x-ndjson");
            string cursor = first.Headers.GetValues("X-Cohesion-Next-Cursor").Single();
            using var second = await client.GetAsync(new Uri(query, "/cohesion/v1/logs?resource=web&limit=1&cursor=" + Uri.EscapeDataString(cursor)), timeout.Token);
            second.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await second.Content.ReadAsStringAsync(timeout.Token)).ShouldNotBe(await first.Content.ReadAsStringAsync(timeout.Token));
            using var filtered = await client.GetAsync(new Uri(query, "/cohesion/v1/logs?resource=other"), timeout.Token);
            (await filtered.Content.ReadAsStringAsync(timeout.Token)).ShouldBeEmpty();
            using var future = await client.GetAsync(new Uri(query, "/cohesion/v1/logs?since=2099-01-01T00:00:00Z"), timeout.Token);
            (await future.Content.ReadAsStringAsync(timeout.Token)).ShouldBeEmpty();
        }
        finally
        {
            timeout.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(20)); } catch (OperationCanceledException) { }
            try
            {
                if (acceptedStartup)
                {
                    string stored = string.Join("", Directory.EnumerateFiles(Path.Combine(root, "telemetry-tests", "logs"), "logs-*.ndjson", SearchOption.AllDirectories).Select(File.ReadAllText));
                    stored.ShouldContain("Resource telemetry stopping", Case.Sensitive);
                }
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Telemetry: Removing the sink removes variables and protected headers")]
    public async Task Injection_AbsentSink_ShouldRemoveEveryCarrier()
    {
        string root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "ti-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var environment = new Dictionary<string, string>();
            var injection = new ResourceTelemetryInjection(new Uri("https://localhost:4318"), Encoding.UTF8.GetBytes("Authorization: Bearer test\n"));
            ResourceTelemetryInjection.Apply(injection, environment);
            environment[AppEnvironment.Variables.TelemetryEndpoint].ShouldBe("https://localhost:4318");
            environment[AppEnvironment.Variables.TelemetryProtocol].ShouldBe("otlp-http");
            var mounts = new LocalMountMaterializer(root);
            await mounts.MaterializeTelemetryHeadersAsync("app", "web", injection.HeadersDocument, environment, CancellationToken.None);
            string path = environment[AppEnvironment.Variables.TelemetryHeadersPath];
            Read(path).ShouldBe("Authorization: Bearer test\n");
            ResourceTelemetryInjection.Apply(null, environment);
            await mounts.MaterializeTelemetryHeadersAsync("app", "web", ReadOnlyMemory<byte>.Empty, environment, CancellationToken.None);
            environment.ShouldBeEmpty(); File.Exists(path).ShouldBeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void InitializeSession(ApplicationGateway gateway, IApplicationModel model) =>
        typeof(ApplicationGateway).GetMethod("InitializeSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(gateway, [new IApplicationModel[] { model }]);

    // The resource sink mints each emitter's credential from the application's trust state.
    private static void InitializeTrust(ApplicationGateway gateway, IApplicationModel model) =>
        ((Task)typeof(ApplicationGateway).GetMethod("EnsureTrustStateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(gateway, [model, CancellationToken.None])!).GetAwaiter().GetResult();

    // Resolution is asynchronous because a resource sink's emitter credential goes through the
    // application's credential issuer; the helper completes it so failures surface directly.
    private static object? ResolveTelemetry(ApplicationGateway gateway, IApplicationModel model, string emitter)
    {
        var pending = (ValueTask<ResourceTelemetryInjection?>)typeof(ApplicationGateway)
            .GetMethod("ResolveTelemetryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(gateway, [model, model.Resources.Single(resource => resource.Name.ToString() == emitter), CancellationToken.None])!;
        return pending.AsTask().GetAwaiter().GetResult();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
    private sealed class OversizedChunkedContent : HttpContent
    {
        internal OversizedChunkedContent() => Headers.ContentType = new MediaTypeHeaderValue("application/json");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            byte[] bytes = new byte[1024 * 1024 + 1];
            await stream.WriteAsync(bytes);
        }
    }
    private static string Read(string path) => Encoding.UTF8.GetString(new HostingMount(path).ReadAllBytes());
    private static Uri Address(ResourceEndpoint endpoint) => Uri.CreateEndpoint(endpoint.Scheme, endpoint.Host!, endpoint.Port);
    private static ResourceManifestEndpoint Endpoint(string name, int port) => new() { Name = name, Scheme = "https", Protocol = "tcp", ContainerPort = port, Certificate = "tls" };
    private static ResourceManifest Manifest(string name, string kind, string endpoint, string assembly) => new()
    {
        Name = name, Application = "telemetry-tests", Kind = kind, ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
        Artifact = new ResourceManifestArtifact { Assembly = assembly + ".dll", AppHost = Path.Combine(AppContext.BaseDirectory, assembly + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)) },
        Endpoints = [Endpoint(endpoint, 8443)],
        Mounts = [new ResourceManifestMount { Name = "tls", Kind = ResourceMountKind.Secret, ContainerPath = "/cohesion/mounts/tls" }],
        ControlPlane = new ResourceManifestControlPlane { Endpoint = endpoint, Path = "/cohesion/v1" },
        Lifecycle = new ResourceManifestLifecycle { Workload = WorkloadKind.Deployment, Replicas = 1, RestartPolicy = "Never", StopGraceSeconds = 5 },
    };
}
