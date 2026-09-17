using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

public sealed class ResourceControlPlaneHostingTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Telemetry off creates no logger factory or extra host service")]
    public async Task TelemetryOff_ShouldPreserveComposition()
    {
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext());
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions(), typeof(ResourceControlPlaneHostingTests).Assembly);
        typeof(DatabaseApplicationBuilder).GetField("_loggerFactory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(builder).ShouldBeNull();
        await using var application = builder.Build();
        await using var baseline = DatabaseApplication.CreateBuilder().Build();
        application.Context.HostedServices.Count().ShouldBe(baseline.Context.HostedServices.Count());
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - CreateBuilder(args): honors the registered control plane and ambient admin endpoint")]
    public async Task CreateBuilderWithArgs_WhenRegistered_ShouldComposeControlPlane()
    {
        Uri endpoint = Uri.CreateEndpoint("http", "127.0.0.1", ReservePort());
        Uri databaseEndpoint = Uri.CreateEndpoint("cohesion-db", "127.0.0.1", ReservePort());
        string contentRootPath = Path.GetFullPath("database-resource-content");
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            environmentName: "ControlPlaneTest",
            contentRootPath: contentRootPath,
            endpoints: new Dictionary<string, Uri>
            {
                ["admin"] = endpoint,
                ["db"] = databaseEndpoint,
            }));

        DatabaseApplicationBuilder builder = new(
            new DatabaseApplicationOptions(),
            typeof(ResourceControlPlaneHostingTests).Assembly);
        builder.AddHealthCheck("builder", _ => ValueTask.FromResult(HealthContribution.Healthy()));
        builder.Options.Services.Add(new HealthyHostService("services"));

        await using DatabaseApplication application = builder.Build();
        IResourceControlPlane controlPlane = builder.ControlPlane.ShouldNotBeNull();
        ResourceHealthReport report = await controlPlane.CheckReadinessAsync(CancellationToken.None);

        controlPlane.ObservedEndpoints["admin"].ShouldBe(endpoint);
        controlPlane.ObservedEndpoints["db"].ShouldBe(databaseEndpoint);
        application.Context.Environment.Name.ShouldBe("ControlPlaneTest");
        application.Context.Environment.ContentRootPath.ShouldBe(
            FileSystemPath.Parse(contentRootPath));
        report.Contributions.Keys.ShouldContain("builder");
        report.Contributions.Keys.ShouldContain("database");
        report.Contributions.Keys.ShouldContain("services");

        await ((IHost)application).StartAsync(CancellationToken.None);
        try
        {
            using var client = new HttpClient { BaseAddress = endpoint };
            foreach (string path in new[]
            {
                "/healthz",
                "/readyz",
                "/livez",
                "/cohesion/v1/healthz",
                "/cohesion/v1/readyz",
                "/cohesion/v1/livez",
                "/cohesion/v1/endpoints",
                "/cohesion/v1/commands",
            })
            {
                using HttpResponseMessage response = await client.GetAsync(path, CancellationToken.None);
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            using JsonDocument health = JsonDocument.Parse(await client.GetStringAsync(
                "/healthz",
                CancellationToken.None));
            JsonElement healthEntries = health.RootElement.GetProperty("entries");
            healthEntries.TryGetProperty("builder", out _).ShouldBeTrue();
            healthEntries.TryGetProperty("database", out _).ShouldBeTrue();
            healthEntries.TryGetProperty("services", out _).ShouldBeTrue();

            using JsonDocument endpoints = JsonDocument.Parse(await client.GetStringAsync(
                "/cohesion/v1/endpoints",
                CancellationToken.None));
            endpoints.RootElement
                .GetProperty("endpoints")
                .GetProperty("admin")
                .GetString()
                .ShouldBe(endpoint.ToEndpointString());
            endpoints.RootElement
                .GetProperty("endpoints")
                .GetProperty("db")
                .GetString()
                .ShouldBe(databaseEndpoint.ToEndpointString());

            using JsonDocument commands = JsonDocument.Parse(await client.GetStringAsync(
                "/cohesion/v1/commands",
                CancellationToken.None));
            commands.RootElement
                .GetProperty("acceptedCommandKinds")
                .GetArrayLength()
                .ShouldBe(2);

            using var content = new StringContent(
                """{"id":"1","kind":"database.add","owner":"test","key":"app","payload":""}""",
                Encoding.UTF8,
                "application/json");
            using HttpResponseMessage commandResponse = await client.PostAsync(
                "/cohesion/v1/commands",
                content,
                CancellationToken.None);
            commandResponse.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Readiness: remains unavailable until every database server accepts")]
    public async Task Readiness_WhileServerBindIsPending_ShouldRemainUnavailable()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Uri endpoint = Uri.CreateEndpoint("http", "127.0.0.1", ReservePort());
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            endpoints: new Dictionary<string, Uri> { ["admin"] = endpoint }));
        var bindStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DatabaseApplicationBuilder builder = new(
            new DatabaseApplicationOptions(),
            typeof(ResourceControlPlaneHostingTests).Assembly);
        builder.AddServer(new ControlledStartServer(bindStarted, accepting));
        await using DatabaseApplication application = builder.Build();
        using var client = new HttpClient { BaseAddress = endpoint };

        Task start = ((IHost)application).StartAsync(cancellation.Token);
        await bindStarted.Task.WaitAsync(cancellation.Token);
        using HttpResponseMessage starting = await WaitForResponseAsync(
            client,
            "/readyz",
            cancellation.Token);

        starting.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        application.Context.State.ShouldBe(HostState.Starting);

        accepting.TrySetResult(true);
        await start;
        using HttpResponseMessage started = await client.GetAsync("/readyz", cancellation.Token);
        started.StatusCode.ShouldBe(HttpStatusCode.OK);

        await ((IHost)application).StopAsync(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Control plane: protects namespaced routes with the ambient bootstrap credential")]
    public async Task ControlPlane_WithBootstrapCredential_ShouldAuthenticateNamespacedRoutes()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Uri endpoint = Uri.CreateEndpoint("http", "127.0.0.1", ReservePort());
        using var identity = new TestBootstrapIdentity("tests", "local");
        string credential = identity.Issue("database");
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            applicationName: "tests", resourceName: "database", environmentName: "Testing", gatewayName: "local",
            contentRootPath: null, endpoints: new Dictionary<string, Uri> { ["admin"] = endpoint },
            mounts: null, settings: null, references: null, bootstrapCredential: Encoding.UTF8.GetBytes(credential),
            applicationTrustKey: identity.PublicKey, ambientValues: null));
        DatabaseApplicationBuilder builder = new(
            new DatabaseApplicationOptions(),
            typeof(ResourceControlPlaneHostingTests).Assembly);
        await using DatabaseApplication application = builder.Build();
        using var client = new HttpClient { BaseAddress = endpoint };

        await ((IHost)application).StartAsync(cancellation.Token);
        try
        {
            // Act
            using HttpResponseMessage bareHealth = await client.GetAsync(
                "/readyz",
                cancellation.Token);
            using HttpResponseMessage anonymous = await client.GetAsync(
                "/cohesion/v1/endpoints",
                cancellation.Token);
            using var authenticatedRequest = new HttpRequestMessage(
                HttpMethod.Get,
                "/cohesion/v1/endpoints");
            authenticatedRequest.Headers.TryAddWithoutValidation(
                "Authorization",
                "Bearer " + credential);
            using HttpResponseMessage authenticated = await client.SendAsync(
                authenticatedRequest,
                cancellation.Token);

            // Assert
            bareHealth.StatusCode.ShouldBe(HttpStatusCode.OK);
            anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            anonymous.Headers.WwwAuthenticate.ShouldContain(
                value => value.Scheme == "Bearer");
            authenticated.StatusCode.ShouldBe(HttpStatusCode.OK);
            using HttpResponseMessage namespacedProbe = await client.GetAsync("/cohesion/v1/readyz", cancellation.Token);
            namespacedProbe.StatusCode.ShouldBe(HttpStatusCode.OK);
            using HttpResponseMessage unknown = await client.GetAsync("/cohesion/v1/unknown", cancellation.Token);
            unknown.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", identity.Issue("other"));
            using HttpResponseMessage forbidden = await client.GetAsync("/cohesion/v1/endpoints", cancellation.Token);
            forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            forbidden.Headers.WwwAuthenticate.ShouldBeEmpty();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
            using HttpResponseMessage stopped = await client.PostAsync("/cohesion/v1/stop", null, cancellation.Token);
            stopped.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Build: validates managed trust only with an admin listener")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Build_WithManagedContextWithoutTrustKey_ShouldValidateOnlyInstalledTerminal(bool listener)
    {
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            applicationName: "tests", resourceName: "database", gatewayName: "local",
            endpoints: listener ? new Dictionary<string, Uri> { ["admin"] = Uri.CreateEndpoint("http", "127.0.0.1", ReservePort()) } : null));
        DatabaseApplicationBuilder builder = new(new DatabaseApplicationOptions(), typeof(ResourceControlPlaneHostingTests).Assembly);
        if (listener)
        {
            InvalidOperationException error = Should.Throw<InvalidOperationException>(() => builder.Build());
            error.Message.ShouldBe("A gateway-managed resource requires a public application trust key.");
        }
        else
        {
            await using DatabaseApplication application = builder.Build();
            builder.ControlPlane.ShouldNotBeNull();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Control plane: unmanaged credential does not enable authentication")]
    public async Task ControlPlane_WithUnmanagedCredential_ShouldRemainOpen()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Uri endpoint = Uri.CreateEndpoint("http", "127.0.0.1", ReservePort());
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            endpoints: new Dictionary<string, Uri> { ["admin"] = endpoint }, bootstrapCredential: "unused"u8.ToArray()));
        DatabaseApplicationBuilder builder = new(new DatabaseApplicationOptions(), typeof(ResourceControlPlaneHostingTests).Assembly);
        await using DatabaseApplication application = builder.Build();
        await ((IHost)application).StartAsync(cancellation.Token);
        using var client = new HttpClient { BaseAddress = endpoint };
        using HttpResponseMessage response = await WaitForResponseAsync(client, "/cohesion/v1/endpoints", cancellation.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await ((IHost)application).StopAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - CreateBuilder(): remains a plain application")]
    public void CreateBuilderWithoutArgs_ShouldRemainPlain()
    {
        DatabaseApplication.CreateBuilder().ControlPlane.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Context health: aggregates distinct engine states and worker inventory")]
    public async Task CheckAsync_WithFaultedServerEngine_ShouldReportDegradedWithWorkerInventory()
    {
        // Arrange: register the same engine directly and behind a server; the context health
        // contribution must count the data machine once while still discovering server engines.
        var worker = new RecordingEngineWorker(
            "sql/wal-flush",
            DatabaseEngineWorkerKind.WriteAheadFlush,
            TimeSpan.FromMilliseconds(25));
        var engine = new RecordingEngine(
            "sql",
            EngineState.Faulted,
            [worker]);
        var options = new DatabaseApplicationOptions();
        options.Engines.Add(engine);
        options.Servers.Add(new RecordingServer([], engine: engine));
        await using var application = new DatabaseApplication(options);

        // Act
        HealthContribution contribution = await application.Context.CheckAsync(CancellationToken.None);

        // Assert
        contribution.Status.ShouldBe(HealthStatus.Degraded);
        IReadOnlyDictionary<string, object> data = contribution.Data.ShouldNotBeNull();
        data["engineCount"].ShouldBe(1);
        data["workerCount"].ShouldBe(1);
        data["engine.0.name"].ShouldBe("sql");
        data["engine.0.state"].ShouldBe(nameof(EngineState.Faulted));
        data["engine.0.worker.0.name"].ShouldBe("sql/wal-flush");
        data["engine.0.worker.0.kind"].ShouldBe(nameof(DatabaseEngineWorkerKind.WriteAheadFlush));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Context health: a disposed engine is unhealthy")]
    public async Task CheckAsync_WithDisposedEngine_ShouldReportUnhealthy()
    {
        // Arrange
        var options = new DatabaseApplicationOptions();
        options.Engines.Add(new RecordingEngine(state: EngineState.Disposed));
        await using var application = new DatabaseApplication(options);

        // Act
        HealthContribution contribution = await application.Context.CheckAsync(CancellationToken.None);

        // Assert
        contribution.Status.ShouldBe(HealthStatus.Unhealthy);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Control plane: stop requests gracefully drain the attached host")]
    public async Task StopEndpoint_WhenPosted_ShouldGracefullyStopAttachedHost()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Uri endpoint = Uri.CreateEndpoint("http", "127.0.0.1", ReservePort());
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            endpoints: new Dictionary<string, Uri> { ["admin"] = endpoint }));
        DatabaseApplicationBuilder builder = new(
            new DatabaseApplicationOptions(),
            typeof(ResourceControlPlaneHostingTests).Assembly);
        await using DatabaseApplication application = builder.Build();
        using var client = new HttpClient { BaseAddress = endpoint };

        // Act
        Task runTask = application.RunAsync(cancellation.Token);
        await WaitUntilReadyAsync(client, cancellation.Token);
        using HttpResponseMessage response = await client.PostAsync(
            "/cohesion/v1/stop",
            content: null,
            cancellation.Token);
        await runTask.WaitAsync(cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        application.Context.State.ShouldBe(HostState.Stopped);
    }

    private sealed class HealthyHostService(string name) : IHostService, IHealthContributor
    {
        public ServiceId Id { get; } = ServiceId.New();

        public string Name { get; } = name;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(HealthContribution.Healthy());
        }
    }

    private sealed class ControlledStartServer(
        TaskCompletionSource<bool> bindStarted,
        TaskCompletionSource<bool> accepting) : IDatabaseServer
    {
        private readonly RecordingServer _inner = new([], "controlled");

        public IDatabaseServerContext Context => _inner.Context;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            bindStarted.TrySetResult(true);
            return accepting.Task.WaitAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task WaitUntilReadyAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync(
                    "/readyz",
                    cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    private static async Task<HttpResponseMessage> WaitForResponseAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return await client.GetAsync(path, cancellationToken);
            }
            catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
        }
    }
}

internal static class ResourceControlPlaneTestRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        ResourceRuntime.RegisterControlPlane(
            typeof(ResourceControlPlaneTestRegistration).Assembly,
            static () => ResourceControlPlane.Create(["database.add-database", "database.add-principal"]));
    }
}
