using System;
using System.Text.Json;
using System.Threading.Tasks;

using Assimalign.Cohesion.Health;
using Assimalign.Cohesion.Http;

using Shouldly;

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

    [Fact(DisplayName = "Cohesion Test [Web.Health] - MapHealthChecks: attaches the health report as an IHttpHealthFeature")]
    public async Task MapHealthChecks_WhenEvaluated_ShouldAttachHealthFeature()
    {
        IHealthCheckService service = HealthChecks.CreateBuilder()
            .AddCheck("db", () => HealthCheckResult.Degraded())
            .Build();

        TestPipelineBuilder builder = new();
        builder.MapHealthChecks(service);

        TestHttpContext context = new("/healthz", HttpMethod.Get);
        await builder.Build().ExecuteAsync(context);

        IHttpHealthFeature? feature = context.Features.Get<IHttpHealthFeature>();
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

    private sealed class TextResponseWriter : IHealthResponseWriter
    {
        public async ValueTask WriteAsync(IHttpContext context, HealthReport report, System.Threading.CancellationToken cancellationToken = default)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(report.Status.ToString());
            await context.Response.Body.WriteAsync(bytes, cancellationToken);
        }
    }
}
