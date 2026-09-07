using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Web;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public sealed class ResourceControlPlaneHostingTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Control plane: terminal routes run ahead of user middleware")]
    public async Task ControlPlaneRoutes_WhenRegistered_ShouldRunAheadOfUserMiddleware()
    {
        int port = ReservePort();
        var endpoint = new EndpointAddress("http", "127.0.0.1", port);
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            endpoints: new Dictionary<string, EndpointAddress> { ["http"] = endpoint }));

        WebApplicationBuilder builder = WebApplication.CreateBuilder([]);
        builder.AddHealthCheck("self", _ => ValueTask.FromResult(HealthContribution.Healthy()));
        await using WebApplication application = builder.Build();
        bool userMiddlewareRan = false;
        application.Use((context, _) =>
        {
            userMiddlewareRan = true;
            context.Response.StatusCode = Assimalign.Cohesion.Http.HttpStatusCode.InternalServerError;
            return Task.CompletedTask;
        });

        await ((IHost)application).StartAsync(CancellationToken.None);
        try
        {
            using var client = new HttpClient { BaseAddress = endpoint.Url };
            foreach (string path in new[]
            {
                "/healthz",
                "/cohesion/v1/healthz",
                "/readyz",
                "/cohesion/v1/readyz",
                "/livez",
                "/cohesion/v1/livez",
                "/cohesion/v1/endpoints",
            })
            {
                using HttpResponseMessage response = await client.GetAsync(path, CancellationToken.None);
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            string endpoints = await client.GetStringAsync(
                "/cohesion/v1/endpoints",
                CancellationToken.None);
            endpoints.ShouldContain(endpoint.ToString());
            userMiddlewareRan.ShouldBeFalse();
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Control plane: a custom pipeline remains behind the fixed terminal")]
    public async Task AddPipeline_WhenControlPlaneIsRegistered_ShouldRemainBehindControlPlaneTerminal()
    {
        int port = ReservePort();
        var endpoint = new EndpointAddress("http", "127.0.0.1", port);
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            endpoints: new Dictionary<string, EndpointAddress> { ["http"] = endpoint },
            bootstrapCredential: "pipeline-token"u8.ToArray()));
        RecordingPipeline pipeline = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder([]);
        ((IWebApplicationBuilder)builder).AddPipeline(pipeline);
        await using WebApplication application = builder.Build();

        await ((IHost)application).StartAsync(CancellationToken.None);
        try
        {
            using var client = new HttpClient { BaseAddress = endpoint.Url };
            using HttpResponseMessage missing = await client.GetAsync(
                "/cohesion/v1/future",
                CancellationToken.None);

            missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            pipeline.ExecuteCount.ShouldBe(0);

            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "pipeline-token");
            using HttpResponseMessage health = await client.GetAsync(
                "/cohesion/v1/healthz",
                CancellationToken.None);

            health.StatusCode.ShouldBe(HttpStatusCode.OK);
            pipeline.ExecuteCount.ShouldBe(0);

            using HttpResponseMessage unknown = await client.GetAsync(
                "/cohesion/v1/future",
                CancellationToken.None);

            unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            pipeline.ExecuteCount.ShouldBe(0);

            client.DefaultRequestHeaders.Authorization = null;
            using HttpResponseMessage user = await client.GetAsync("/user", CancellationToken.None);

            user.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            pipeline.ExecuteCount.ShouldBe(1);
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Control plane: readiness reflects builder health contributions")]
    public async Task Readiness_WhenBuilderContributionIsUnhealthy_ShouldReturnServiceUnavailable()
    {
        int port = ReservePort();
        var endpoint = new EndpointAddress("http", "127.0.0.1", port);
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            endpoints: new Dictionary<string, EndpointAddress> { ["http"] = endpoint }));
        WebApplicationBuilder builder = WebApplication.CreateBuilder([]);
        builder.AddHealthCheck(
            "database",
            _ => ValueTask.FromResult(HealthContribution.Unhealthy("not connected")));
        await using WebApplication application = builder.Build();

        await ((IHost)application).StartAsync(CancellationToken.None);
        try
        {
            using var client = new HttpClient { BaseAddress = endpoint.Url };
            using HttpResponseMessage response = await client.GetAsync(
                "/readyz",
                CancellationToken.None);
            string payload = await response.Content.ReadAsStringAsync(CancellationToken.None);

            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            payload.ShouldContain("database");
            payload.ShouldContain("not connected");
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Control plane: readiness waits for every registered server")]
    public async Task Readiness_WhileAdditionalServerIsStarting_ShouldReturnServiceUnavailable()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int port = ReservePort();
        var endpoint = new EndpointAddress("http", "127.0.0.1", port);
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            endpoints: new Dictionary<string, EndpointAddress> { ["http"] = endpoint }));
        BlockingApplicationServer additionalServer = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder([]);
        ((IWebApplicationBuilder)builder).AddServer(additionalServer);
        await using WebApplication application = builder.Build();
        Task start = ((IHost)application).StartAsync(cancellation.Token);

        try
        {
            await additionalServer.StartEntered.WaitAsync(cancellation.Token);
            using var client = new HttpClient { BaseAddress = endpoint.Url };
            using HttpResponseMessage starting = await client.GetAsync(
                "/readyz",
                cancellation.Token);
            string startingPayload = await starting.Content.ReadAsStringAsync(cancellation.Token);

            starting.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            startingPayload.ShouldContain("cohesion.host");

            additionalServer.ReleaseStart();
            await start.WaitAsync(cancellation.Token);

            using HttpResponseMessage started = await client.GetAsync(
                "/readyz",
                cancellation.Token);
            started.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            additionalServer.ReleaseStart();
            await start.WaitAsync(cancellation.Token);
            if (application.Context.State is HostState.Started)
            {
                await ((IHost)application).StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Control plane: bootstrap credential protects namespaced routes")]
    public async Task ControlPlane_WhenBootstrapCredentialExists_ShouldRequireBearerCredential()
    {
        int port = ReservePort();
        var endpoint = new EndpointAddress("http", "127.0.0.1", port);
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            endpoints: new Dictionary<string, EndpointAddress> { ["http"] = endpoint },
            bootstrapCredential: "secret-token"u8.ToArray()));
        WebApplicationBuilder builder = WebApplication.CreateBuilder([]);
        await using WebApplication application = builder.Build();

        await ((IHost)application).StartAsync(CancellationToken.None);
        try
        {
            using var client = new HttpClient { BaseAddress = endpoint.Url };
            using HttpResponseMessage bareProbe = await client.GetAsync(
                "/readyz",
                CancellationToken.None);
            using HttpResponseMessage missing = await client.GetAsync(
                "/cohesion/v1/endpoints",
                CancellationToken.None);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong-token");
            using HttpResponseMessage wrong = await client.GetAsync(
                "/cohesion/v1/endpoints",
                CancellationToken.None);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret-token");
            using HttpResponseMessage accepted = await client.GetAsync(
                "/cohesion/v1/endpoints",
                CancellationToken.None);

            bareProbe.StatusCode.ShouldBe(HttpStatusCode.OK);
            missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            missing.Headers.WwwAuthenticate.ShouldHaveSingleItem().Scheme.ShouldBe("Bearer");
            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Control plane: a managed context without a credential fails closed")]
    public async Task ControlPlane_WhenManagedContextHasNoCredential_ShouldRejectNamespacedRoutes()
    {
        int port = ReservePort();
        var endpoint = new EndpointAddress("http", "127.0.0.1", port);
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            gatewayName: "inprocess",
            endpoints: new Dictionary<string, EndpointAddress> { ["http"] = endpoint }));
        WebApplicationBuilder builder = WebApplication.CreateBuilder([]);
        await using WebApplication application = builder.Build();

        await ((IHost)application).StartAsync(CancellationToken.None);
        try
        {
            using var client = new HttpClient { BaseAddress = endpoint.Url };
            using HttpResponseMessage bareProbe = await client.GetAsync(
                "/readyz",
                CancellationToken.None);
            using HttpResponseMessage namespaced = await client.GetAsync(
                "/cohesion/v1/endpoints",
                CancellationToken.None);

            bareProbe.StatusCode.ShouldBe(HttpStatusCode.OK);
            namespaced.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            namespaced.Headers.WwwAuthenticate.ShouldHaveSingleItem().Scheme.ShouldBe("Bearer");
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Control plane: stop route completes a resource RunAsync lifecycle")]
    public async Task StopRoute_DuringRunAsync_ShouldStopResourceAndReleaseListener()
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        int port = ReservePort();
        var endpoint = new EndpointAddress("http", "127.0.0.1", port);
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            endpoints: new Dictionary<string, EndpointAddress> { ["http"] = endpoint }));
        WebApplicationBuilder builder = WebApplication.CreateBuilder([]);
        await using WebApplication application = builder.Build();

        Task run = application.RunAsync(cancellation.Token);
        try
        {
            using var client = new HttpClient { BaseAddress = endpoint.Url };
            await WaitUntilReadyAsync(client, cancellation.Token);

            using HttpResponseMessage response = await client.PostAsync(
                "/cohesion/v1/stop",
                content: null,
                cancellation.Token);

            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            await run.WaitAsync(cancellation.Token);
            application.Context.State.ShouldBe(HostState.Stopped);

            using var replacement = new TcpListener(IPAddress.Loopback, port);
            replacement.Start();
        }
        finally
        {
            if (!run.IsCompleted)
            {
                await ((IHost)application).StopAsync(CancellationToken.None);
                await run.WaitAsync(cancellation.Token);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Control plane: parallel ambient scopes produce isolated Web hosts")]
    public async Task CreateBuilderWithArgs_InParallelScopes_ShouldIsolateHosts()
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        (int firstPort, int secondPort) = ReserveDistinctPorts();
        var firstEndpoint = new EndpointAddress("http", "127.0.0.1", firstPort);
        var secondEndpoint = new EndpointAddress("http", "127.0.0.1", secondPort);

        Task<(WebApplication Application, IResourceControlPlane ControlPlane)> firstBuild = Task.Run(
            () => BuildScopedApplication("FirstScope", "first", firstEndpoint),
            cancellation.Token);
        Task<(WebApplication Application, IResourceControlPlane ControlPlane)> secondBuild = Task.Run(
            () => BuildScopedApplication("SecondScope", "second", secondEndpoint),
            cancellation.Token);
        (WebApplication first, IResourceControlPlane firstPlane) = await firstBuild;
        (WebApplication second, IResourceControlPlane secondPlane) = await secondBuild;

        await using (first)
        await using (second)
        {
            firstPlane.ShouldNotBeSameAs(secondPlane);
            first.Context.Environment.Name.ShouldBe("FirstScope");
            second.Context.Environment.Name.ShouldBe("SecondScope");

            await Task.WhenAll(
                ((IHost)first).StartAsync(cancellation.Token),
                ((IHost)second).StartAsync(cancellation.Token));

            try
            {
                using var firstClient = new HttpClient { BaseAddress = firstEndpoint.Url };
                using var secondClient = new HttpClient { BaseAddress = secondEndpoint.Url };

                string firstHealth = await firstClient.GetStringAsync(
                    "/cohesion/v1/healthz",
                    cancellation.Token);
                string secondHealth = await secondClient.GetStringAsync(
                    "/cohesion/v1/healthz",
                    cancellation.Token);
                string firstEndpoints = await firstClient.GetStringAsync(
                    "/cohesion/v1/endpoints",
                    cancellation.Token);
                string secondEndpoints = await secondClient.GetStringAsync(
                    "/cohesion/v1/endpoints",
                    cancellation.Token);

                firstHealth.ShouldContain("first");
                firstHealth.ShouldNotContain("second");
                secondHealth.ShouldContain("second");
                secondHealth.ShouldNotContain("first");
                firstEndpoints.ShouldContain(firstEndpoint.ToString());
                firstEndpoints.ShouldNotContain(secondEndpoint.ToString());
                secondEndpoints.ShouldContain(secondEndpoint.ToString());
                secondEndpoints.ShouldNotContain(firstEndpoint.ToString());
            }
            finally
            {
                await Task.WhenAll(
                    ((IHost)first).StopAsync(CancellationToken.None),
                    ((IHost)second).StopAsync(CancellationToken.None));
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - CreateBuilder(args): honors the registered control plane and ambient endpoint")]
    public async Task CreateBuilderWithArgs_WhenRegistered_ShouldComposeControlPlane()
    {
        var endpoint = new EndpointAddress("http", "127.0.0.1", 48123);
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            environmentName: "ControlPlaneTest",
            endpoints: new Dictionary<string, EndpointAddress> { ["http"] = endpoint }));

        WebApplicationBuilder builder = WebApplication.CreateBuilder([]);
        builder.AddHealthCheck("builder", _ => ValueTask.FromResult(HealthContribution.Healthy()));
        builder.Services.AddSingleton<IHealthContributor>(new HealthyContributor("services"));

        await using WebApplication application = builder.Build();
        IResourceControlPlane controlPlane = builder.ControlPlane.ShouldNotBeNull();
        ResourceHealthReport report = await controlPlane.CheckHealthAsync(CancellationToken.None);

        controlPlane.ObservedEndpoints["http"].ShouldBe(endpoint);
        application.Context.Environment.Name.ShouldBe("ControlPlaneTest");
        report.Contributions.Keys.ShouldContain("builder");
        report.Contributions.Keys.ShouldContain("services");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - CreateBuilder(): remains a plain application")]
    public void CreateBuilderWithoutArgs_ShouldRemainPlain()
    {
        WebApplication.CreateBuilder().ControlPlane.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - CreateBuilder(args): an unregistered caller remains plain with ambient context")]
    public async Task CreateBuilderWithArgs_WhenCallerIsUnregistered_ShouldRemainPlain()
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        using var ambientReservation = new TcpListener(IPAddress.Loopback, 0);
        ambientReservation.Start();
        int ambientPort = ((IPEndPoint)ambientReservation.LocalEndpoint).Port;
        int applicationPort = ReservePort();

        var ambientEndpoint = new EndpointAddress("http", "127.0.0.1", ambientPort);
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            environmentName: "AmbientOnly",
            endpoints: new Dictionary<string, EndpointAddress> { ["http"] = ambientEndpoint }));

        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            [],
            typeof(WebApplication).Assembly);
        builder.Server.UseServer(options => options.UseHttp1(
            tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, applicationPort)));

        builder.ControlPlane.ShouldBeNull();
        builder.Environment.Name.ShouldNotBe("AmbientOnly");

        await using WebApplication application = builder.Build();
        await ((IHost)application).StartAsync(cancellation.Token);
        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{applicationPort}"),
            };
            foreach (string path in new[] { "/healthz", "/cohesion/v1/endpoints" })
            {
                using HttpResponseMessage response = await client.GetAsync(path, cancellation.Token);
                response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            }
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
        }
    }

    private sealed class HealthyContributor(string name) : IHealthContributor
    {
        public string Name { get; } = name;

        public ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(HealthContribution.Healthy());
        }
    }

    private sealed class BlockingApplicationServer : IWebApplicationServer
    {
        private readonly TaskCompletionSource _startEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseStart = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task StartEntered => _startEntered.Task;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            _startEntered.TrySetResult();
            await _releaseStart.Task.WaitAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        internal void ReleaseStart()
        {
            _releaseStart.TrySetResult();
        }
    }

    private sealed class RecordingPipeline : IWebApplicationPipeline
    {
        private int _executeCount;

        internal int ExecuteCount => Volatile.Read(ref _executeCount);

        public Task ExecuteAsync(
            Assimalign.Cohesion.Http.IHttpContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _executeCount);
            context.Response.StatusCode = Assimalign.Cohesion.Http.HttpStatusCode.NoContent;
            return Task.CompletedTask;
        }
    }

    private static (WebApplication Application, IResourceControlPlane ControlPlane) BuildScopedApplication(
        string environmentName,
        string healthName,
        EndpointAddress endpoint)
    {
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            environmentName: environmentName,
            endpoints: new Dictionary<string, EndpointAddress> { ["http"] = endpoint }));
        WebApplicationBuilder builder = WebApplication.CreateBuilder([]);
        builder.AddHealthCheck(
            healthName,
            _ => ValueTask.FromResult(HealthContribution.Healthy()));
        WebApplication application = builder.Build();
        return (application, builder.ControlPlane.ShouldNotBeNull());
    }

    private static async Task WaitUntilReadyAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using HttpResponseMessage response = await client.GetAsync(
                    "/cohesion/v1/readyz",
                    cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The listener has not completed binding yet.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    private static (int First, int Second) ReserveDistinctPorts()
    {
        var first = new TcpListener(IPAddress.Loopback, 0);
        var second = new TcpListener(IPAddress.Loopback, 0);
        first.Start();
        second.Start();
        try
        {
            return (
                ((IPEndPoint)first.LocalEndpoint).Port,
                ((IPEndPoint)second.LocalEndpoint).Port);
        }
        finally
        {
            second.Stop();
            first.Stop();
        }
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
}

internal static class ResourceControlPlaneTestRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        ResourceRuntime.RegisterControlPlane(
            typeof(ResourceControlPlaneTestRegistration).Assembly,
            static () => ResourceControlPlane.Create());
    }
}
