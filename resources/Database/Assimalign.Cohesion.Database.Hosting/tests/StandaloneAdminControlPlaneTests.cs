using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Hosting.Internal;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

public sealed class StandaloneAdminControlPlaneTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Admin: standalone remote health aliases cannot bypass the loopback rule")]
    [InlineData("/cohesion/v1/healthz", "GET")]
    [InlineData("/cohesion/v1/healthz", "HEAD")]
    [InlineData("/cohesion/v1/readyz", "GET")]
    [InlineData("/cohesion/v1/readyz", "HEAD")]
    [InlineData("/cohesion/v1/livez", "GET")]
    [InlineData("/cohesion/v1/livez", "HEAD")]
    public async Task ExecuteAsync_StandaloneRemoteProbeWithForwardedLoopback_ShouldForbid(string path, string method)
    {
        // Arrange
        await using DatabaseApplication application = DatabaseApplication.CreateBuilder().Build();
        using DatabaseAdminEndpointService service = CreateService(application);
        IWebApplicationPipeline pipeline = service.Pipeline.Build();
        await using var exchange = new DatabaseControlPlaneExchange(
            path, new HttpMethod(method), new IPEndPoint(IPAddress.Parse("192.0.2.10"), 41000));
        exchange.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        exchange.Request.Headers["Forwarded"] = "for=\"[::1]\"";

        // Act
        await pipeline.ExecuteAsync(exchange, CancellationToken.None);

        // Assert
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        exchange.Response.Body.Length.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Admin: standalone loopback probes retain Database health reports")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task ExecuteAsync_StandaloneLoopbackProbe_ShouldKeepDatabaseReport(string peer)
    {
        // Arrange
        await using DatabaseApplication application = DatabaseApplication.CreateBuilder().Build();
        using DatabaseAdminEndpointService service = CreateService(application);
        IWebApplicationPipeline pipeline = service.Pipeline.Build();
        foreach (string probe in new[] { "healthz", "readyz", "livez" })
        {
            await using var exchange = new DatabaseControlPlaneExchange(
                "/cohesion/v1/" + probe, HttpMethod.Get, new IPEndPoint(IPAddress.Parse(peer), 41000));

            // Act
            await pipeline.ExecuteAsync(exchange, CancellationToken.None);

            // Assert
            exchange.Response.StatusCode.ShouldBe(probe == "livez" ? HttpStatusCode.Ok : HttpStatusCode.ServiceUnavailable);
            using JsonDocument report = JsonDocument.Parse(((MemoryStream)exchange.Response.Body).ToArray());
            report.RootElement.TryGetProperty("entries", out _).ShouldBeTrue();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Admin: standalone bare probes remain open to remote callers")]
    [InlineData("/healthz", 503)]
    [InlineData("/readyz", 503)]
    [InlineData("/livez", 200)]
    public async Task ExecuteAsync_StandaloneRemoteBareProbe_ShouldRemainOpen(string path, int expected)
    {
        // Arrange
        await using DatabaseApplication application = DatabaseApplication.CreateBuilder().Build();
        using DatabaseAdminEndpointService service = CreateService(application);
        IWebApplicationPipeline pipeline = service.Pipeline.Build();
        await using var exchange = new DatabaseControlPlaneExchange(
            path, HttpMethod.Get, new IPEndPoint(IPAddress.Parse("192.0.2.10"), 41000));

        // Act
        await pipeline.ExecuteAsync(exchange, CancellationToken.None);

        // Assert
        exchange.Response.StatusCode.ShouldBe(new HttpStatusCode(expected));
        exchange.Response.Body.Length.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// The resource control plane answered the rest of <c>/cohesion/v1</c> and authenticated its
    /// callers only with the resource bootstrap credential, both retired with the application-model
    /// restart (#1759). A loopback caller, with a credential or without one, is refused.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Admin: retired control-plane routes fail closed for loopback callers")]
    [InlineData("/cohesion/v1", "GET")]
    [InlineData("/cohesion/v1/endpoints", "GET")]
    [InlineData("/cohesion/v1/commands", "GET")]
    [InlineData("/cohesion/v1/commands", "POST")]
    [InlineData("/cohesion/v1/stop", "POST")]
    [InlineData("/cohesion/v1/unknown", "GET")]
    public async Task ExecuteAsync_RetiredControlPlaneRoute_ShouldFailClosed(string path, string method)
    {
        // Arrange
        await using DatabaseApplication application = DatabaseApplication.CreateBuilder().Build();
        using DatabaseAdminEndpointService service = CreateService(application);
        IWebApplicationPipeline pipeline = service.Pipeline.Build();
        await using var anonymous = new DatabaseControlPlaneExchange(
            path, new HttpMethod(method), new IPEndPoint(IPAddress.Loopback, 41000));
        await using var credentialed = new DatabaseControlPlaneExchange(
            path, new HttpMethod(method), new IPEndPoint(IPAddress.Loopback, 41001));
        credentialed.Request.Headers["Authorization"] = "Bearer a.b.c";

        // Act
        await pipeline.ExecuteAsync(anonymous, CancellationToken.None);
        await pipeline.ExecuteAsync(credentialed, CancellationToken.None);

        // Assert
        anonymous.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        anonymous.Response.Body.Length.ShouldBe(0);
        credentialed.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        credentialed.Response.Body.Length.ShouldBe(0);
        application.Context.State.ShouldBe(HostState.Idle);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Admin: an endpoint other than plain http is refused")]
    [InlineData("https://127.0.0.1:8080")]
    [InlineData("tcp://127.0.0.1:8080")]
    public async Task Constructor_NonHttpEndpoint_ShouldRefuse(string endpoint)
    {
        // Arrange
        await using DatabaseApplication application = DatabaseApplication.CreateBuilder().Build();

        // Act / Assert: an https endpoint's certificate came from the retired resource context.
        Should.Throw<InvalidOperationException>(() => new DatabaseAdminEndpointService(new Uri(endpoint), application.Context));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Readiness: remains unavailable until every database server accepts")]
    public async Task Readiness_WhileServerBindIsPending_ShouldRemainUnavailable()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Uri endpoint = Uri.CreateEndpoint("http", "127.0.0.1", ReservePort());
        var bindStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions());
        await using var engine = new RecordingEngine();
        engine.AddServer(owner => new ControlledStartServer(owner, bindStarted, accepting));
        builder.AddEngine(engine);
        builder.AddService(context => new DatabaseAdminEndpointService(endpoint, context));
        await using DatabaseApplication application = builder.Build();
        using var client = new System.Net.Http.HttpClient { BaseAddress = endpoint };

        // Act
        Task start = ((IHost)application).StartAsync(cancellation.Token);
        await bindStarted.Task.WaitAsync(cancellation.Token);
        using System.Net.Http.HttpResponseMessage starting = await WaitForResponseAsync(
            client,
            "/readyz",
            cancellation.Token);
        HostState whileStarting = application.Context.State;

        accepting.TrySetResult(true);
        await start;
        using System.Net.Http.HttpResponseMessage started = await client.GetAsync("/readyz", cancellation.Token);

        // Assert
        starting.StatusCode.ShouldBe(System.Net.HttpStatusCode.ServiceUnavailable);
        whileStarting.ShouldBe(HostState.Starting);
        started.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        await ((IHost)application).StopAsync(cancellation.Token);
    }

    private static DatabaseAdminEndpointService CreateService(DatabaseApplication application)
    {
        return new DatabaseAdminEndpointService(
            new Uri("http://127.0.0.1:8080"),
            application.Context);
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

    private static async Task<System.Net.Http.HttpResponseMessage> WaitForResponseAsync(
        System.Net.Http.HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return await client.GetAsync(path, cancellationToken);
            }
            catch (System.Net.Http.HttpRequestException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
        }
    }

    private sealed class ControlledStartServer : DatabaseServer
    {
        private readonly TaskCompletionSource<bool> _bindStarted;
        private readonly TaskCompletionSource<bool> _accepting;

        /// <summary>Initializes a new instance of the <see cref="ControlledStartServer"/> class.</summary>
        /// <param name="engine">The engine the server fronts, which attaches it.</param>
        /// <param name="bindStarted">Completed when the host begins starting the server.</param>
        /// <param name="accepting">Completes the server start once it is set, signalling the server is accepting.</param>
        public ControlledStartServer(
            DatabaseEngine engine,
            TaskCompletionSource<bool> bindStarted,
            TaskCompletionSource<bool> accepting)
            : base(engine)
        {
            _bindStarted = bindStarted;
            _accepting = accepting;
        }

        public override IReadOnlyCollection<DatabaseServerSession> Sessions => [];

        protected override Task StartCoreAsync(CancellationToken cancellationToken)
        {
            _bindStarted.TrySetResult(true);
            return _accepting.Task.WaitAsync(cancellationToken);
        }

        protected override Task StopCoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
