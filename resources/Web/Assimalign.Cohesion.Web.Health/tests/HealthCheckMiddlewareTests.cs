using System;
using System.Text.Json;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

using Shouldly;
using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Health.Tests;

public class HealthCheckMiddlewareTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Health] - MapHealthChecks: a healthy report returns 200 with JSON and short-circuits")]
    public async Task MapHealthChecks_WhenHealthy_ShouldReturn200AndShortCircuit()
    {
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("db", () => HealthCheckResult.Healthy("connected"))
            .Build();

        bool downstreamRan = false;
        TestPipelineBuilder builder = new();
        builder.MapHealthChecks(service);
        builder.Use((context, next) =>
        {
            downstreamRan = true;
            return next.Invoke(context);
        });

        TestHttpContext context = new("/healthz", HttpMethod.Get);
        await builder.Build().ExecuteAsync(context);

        context.Response.StatusCode.Value.ShouldBe(200);
        downstreamRan.ShouldBeFalse();

        using JsonDocument document = JsonDocument.Parse(context.ReadResponseBody());
        document.RootElement.GetProperty("status").GetString().ShouldBe("Healthy");
        document.RootElement.GetProperty("entries").GetProperty("db").GetProperty("status").GetString().ShouldBe("Healthy");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - MapHealthChecks: an unhealthy report returns 503")]
    public async Task MapHealthChecks_WhenUnhealthy_ShouldReturn503()
    {
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("db", () => HealthCheckResult.Unhealthy("down"))
            .Build();

        TestPipelineBuilder builder = new();
        builder.MapHealthChecks(service);

        TestHttpContext context = new("/healthz", HttpMethod.Get);
        await builder.Build().ExecuteAsync(context);

        context.Response.StatusCode.Value.ShouldBe(503);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - MapHealthChecks: a degraded report returns 200 by default")]
    public async Task MapHealthChecks_WhenDegraded_ShouldReturn200()
    {
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("cache", () => HealthCheckResult.Degraded("slow"))
            .Build();

        TestPipelineBuilder builder = new();
        builder.MapHealthChecks(service);

        TestHttpContext context = new("/healthz", HttpMethod.Get);
        await builder.Build().ExecuteAsync(context);

        context.Response.StatusCode.Value.ShouldBe(200);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - MapHealthChecks: a non-matching path passes through to downstream middleware")]
    public async Task MapHealthChecks_WhenPathDoesNotMatch_ShouldPassThrough()
    {
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("db", () => HealthCheckResult.Unhealthy())
            .Build();

        bool downstreamRan = false;
        TestPipelineBuilder builder = new();
        builder.MapHealthChecks(service);
        builder.Use((context, next) =>
        {
            downstreamRan = true;
            return next.Invoke(context);
        });

        TestHttpContext context = new("/api/orders", HttpMethod.Get);
        await builder.Build().ExecuteAsync(context);

        downstreamRan.ShouldBeTrue();
        context.Response.StatusCode.Value.ShouldBe(200); // untouched by the health middleware
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - MapHealthChecks: a non-GET/HEAD method passes through")]
    public async Task MapHealthChecks_WhenMethodNotGetOrHead_ShouldPassThrough()
    {
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("db", () => HealthCheckResult.Healthy())
            .Build();

        bool downstreamRan = false;
        TestPipelineBuilder builder = new();
        builder.MapHealthChecks(service);
        builder.Use((context, next) =>
        {
            downstreamRan = true;
            return next.Invoke(context);
        });

        TestHttpContext context = new("/healthz", HttpMethod.Post);
        await builder.Build().ExecuteAsync(context);

        downstreamRan.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - MapHealthChecks: emits no-store Cache-Control by default")]
    public async Task MapHealthChecks_ByDefault_ShouldEmitNoStoreCacheControl()
    {
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("db", () => HealthCheckResult.Healthy())
            .Build();

        TestPipelineBuilder builder = new();
        builder.MapHealthChecks(service);

        TestHttpContext context = new("/healthz", HttpMethod.Get);
        await builder.Build().ExecuteAsync(context);

        context.Response.Headers[HttpHeaderKey.CacheControl].Value.ShouldContain("no-store");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - MapHealthChecks: attaches the health report as an IHealthFeature")]
    public async Task MapHealthChecks_WhenEvaluated_ShouldAttachHealthFeature()
    {
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("db", () => HealthCheckResult.Degraded())
            .Build();

        TestPipelineBuilder builder = new();
        builder.MapHealthChecks(service);

        TestHttpContext context = new("/healthz", HttpMethod.Get);
        await builder.Build().ExecuteAsync(context);

        IHealthFeature? feature = context.Features.Get<IHealthFeature>();
        feature.ShouldNotBeNull();
        feature!.Report.Status.ShouldBe(HealthStatus.Degraded);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - MapReadinessCheck: runs only ready-tagged checks")]
    public async Task MapReadinessCheck_ShouldRunOnlyReadyTaggedChecks()
    {
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("ready-db", () => HealthCheckResult.Healthy(), tags: new[] { HealthTags.Ready })
            .AddCheck("live-self", () => HealthCheckResult.Unhealthy(), tags: new[] { HealthTags.Live })
            .Build();

        TestPipelineBuilder builder = new();
        builder.MapReadinessCheck(service);

        TestHttpContext context = new("/readyz", HttpMethod.Get);
        await builder.Build().ExecuteAsync(context);

        context.Response.StatusCode.Value.ShouldBe(200);

        using JsonDocument document = JsonDocument.Parse(context.ReadResponseBody());
        JsonElement entries = document.RootElement.GetProperty("entries");
        entries.TryGetProperty("ready-db", out _).ShouldBeTrue();
        entries.TryGetProperty("live-self", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - MapLivenessCheck: with no live-tagged checks reports the process up (200)")]
    public async Task MapLivenessCheck_WhenNoLiveChecks_ShouldReturn200()
    {
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("ready-db", () => HealthCheckResult.Unhealthy(), tags: new[] { HealthTags.Ready })
            .Build();

        TestPipelineBuilder builder = new();
        builder.MapLivenessCheck(service);

        TestHttpContext context = new("/livez", HttpMethod.Get);
        await builder.Build().ExecuteAsync(context);

        context.Response.StatusCode.Value.ShouldBe(200);

        using JsonDocument document = JsonDocument.Parse(context.ReadResponseBody());
        document.RootElement.GetProperty("status").GetString().ShouldBe("Healthy");
        document.RootElement.GetProperty("entries").EnumerateObject().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - MapHealthChecks: a custom response writer overrides the default")]
    public async Task MapHealthChecks_WhenCustomWriter_ShouldUseIt()
    {
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("db", () => HealthCheckResult.Healthy())
            .Build();

        TestPipelineBuilder builder = new();
        builder.MapHealthChecks(service, options => options.ResponseWriter = new TextResponseWriter());

        TestHttpContext context = new("/healthz", HttpMethod.Get);
        await builder.Build().ExecuteAsync(context);

        context.ReadResponseBody().ShouldBe("Healthy");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - MapHealthChecks: Should not publish a thrown check's exception message")]
    public async Task MapHealthChecks_WhenCheckThrowsWithHostName_ShouldOmitHostNameFromResponse()
    {
        // Arrange
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("db", StubCheck.Throwing(new InvalidOperationException(ThrownHostMessage)))
            .Build();
        TestPipelineBuilder builder = new();
        builder.MapHealthChecks(service);
        TestHttpContext context = new("/healthz", HttpMethod.Get);

        // Act
        await builder.Build().ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(503);
        string body = context.ReadResponseBody();
        body.ShouldNotContain(HostName, Case.Sensitive);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement entry = document.RootElement.GetProperty("entries").GetProperty("db");
        entry.GetProperty("status").GetString().ShouldBe("Unhealthy");
        entry.GetProperty("description").GetString().ShouldBe(Internal.HealthCheckService.ThrewDescription);
        entry.TryGetProperty("exception", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - MapHealthChecks: Should not publish a timed-out check's exception message")]
    public async Task MapHealthChecks_WhenCheckTimesOutWithHostName_ShouldOmitHostNameFromResponse()
    {
        // Arrange
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck(
                "db",
                StubCheck.BlockingThenCancelledWith(TimedOutHostMessage),
                timeout: TimeSpan.FromMilliseconds(50))
            .Build();
        TestPipelineBuilder builder = new();
        builder.MapHealthChecks(service);
        TestHttpContext context = new("/healthz", HttpMethod.Get);

        // Act
        await builder.Build().ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(503);
        string body = context.ReadResponseBody();
        body.ShouldNotContain(HostName, Case.Sensitive);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement entry = document.RootElement.GetProperty("entries").GetProperty("db");
        entry.GetProperty("description").GetString().ShouldBe(Internal.HealthCheckService.TimedOutDescription);
        entry.TryGetProperty("exception", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - MapHealthChecks: Should not publish a returned result's exception message")]
    public async Task MapHealthChecks_WhenCheckReturnsExceptionMessage_ShouldOmitHostNameFromResponse()
    {
        // Arrange
        var exception = new InvalidOperationException(ThrownHostMessage);
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("db", () => HealthCheckResult.Unhealthy(exception.Message, exception))
            .Build();
        TestPipelineBuilder builder = new();
        builder.MapHealthChecks(service);
        TestHttpContext context = new("/healthz", HttpMethod.Get);

        // Act
        await builder.Build().ExecuteAsync(context);

        // Assert
        context.ReadResponseBody().ShouldNotContain(HostName, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - MapHealthChecks: Should publish a thrown check's exception message with developer details")]
    public async Task MapHealthChecks_WhenDeveloperDetailsAndCheckThrows_ShouldPublishExceptionMessage()
    {
        // Arrange
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("db", StubCheck.Throwing(new InvalidOperationException(ThrownHostMessage)))
            .Build();
        TestPipelineBuilder builder = new();
        builder.MapHealthChecks(service, options => options.IncludeDeveloperDetails = true);
        TestHttpContext context = new("/healthz", HttpMethod.Get);

        // Act
        await builder.Build().ExecuteAsync(context);

        // Assert
        using JsonDocument document = JsonDocument.Parse(context.ReadResponseBody());
        JsonElement entry = document.RootElement.GetProperty("entries").GetProperty("db");
        entry.GetProperty("exception").GetString().ShouldBe(ThrownHostMessage);
        entry.GetProperty("description").GetString().ShouldBe(Internal.HealthCheckService.ThrewDescription);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - MapHealthChecks: Should publish a timed-out check's exception message with developer details")]
    public async Task MapHealthChecks_WhenDeveloperDetailsAndCheckTimesOut_ShouldPublishExceptionMessage()
    {
        // Arrange
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck(
                "db",
                StubCheck.BlockingThenCancelledWith(TimedOutHostMessage),
                timeout: TimeSpan.FromMilliseconds(50))
            .Build();
        TestPipelineBuilder builder = new();
        builder.MapHealthChecks(service, options => options.IncludeDeveloperDetails = true);
        TestHttpContext context = new("/healthz", HttpMethod.Get);

        // Act
        await builder.Build().ExecuteAsync(context);

        // Assert
        using JsonDocument document = JsonDocument.Parse(context.ReadResponseBody());
        document.RootElement.GetProperty("entries").GetProperty("db")
            .GetProperty("exception").GetString().ShouldBe(TimedOutHostMessage);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - MapHealthChecks: Should leave a custom writer in place when developer details are on")]
    public async Task MapHealthChecks_WhenDeveloperDetailsAndCustomWriter_ShouldUseCustomWriter()
    {
        // Arrange
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("db", StubCheck.Throwing(new InvalidOperationException(ThrownHostMessage)))
            .Build();
        TestPipelineBuilder builder = new();
        builder.MapHealthChecks(service, options =>
        {
            options.IncludeDeveloperDetails = true;
            options.ResponseWriter = new TextResponseWriter();
        });
        TestHttpContext context = new("/healthz", HttpMethod.Get);

        // Act
        await builder.Build().ExecuteAsync(context);

        // Assert
        context.ReadResponseBody().ShouldBe("Unhealthy");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - MapHealthChecks: Should keep a thrown check's exception on the health feature's report")]
    public async Task MapHealthChecks_WhenCheckThrows_ShouldKeepExceptionOnHealthFeature()
    {
        // Arrange
        var exception = new InvalidOperationException(ThrownHostMessage);
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("db", StubCheck.Throwing(exception))
            .Build();
        TestPipelineBuilder builder = new();
        builder.MapHealthChecks(service);
        TestHttpContext context = new("/healthz", HttpMethod.Get);

        // Act
        await builder.Build().ExecuteAsync(context);

        // Assert
        IHealthFeature? feature = context.Features.Get<IHealthFeature>();
        feature.ShouldNotBeNull();
        feature!.Report.Entries["db"].Exception.ShouldBeSameAs(exception);
    }

    private const string HostName = "db-primary.corp.internal";

    private const string ThrownHostMessage = "Failed to connect to db-primary.corp.internal:5432 as svc_orders.";

    private const string TimedOutHostMessage = "Connect to db-primary.corp.internal:5432 timed out.";

    private sealed class TextResponseWriter : IHealthResponseWriter
    {
        public async ValueTask WriteAsync(IHttpContext context, HealthReport report, System.Threading.CancellationToken cancellationToken = default)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(report.Status.ToString());
            await context.Response.Body.WriteAsync(bytes, cancellationToken);
        }
    }
}
