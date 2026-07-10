using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Health.Internal;

using Shouldly;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Health.Tests;

public class HealthCheckJsonResponseWriterTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Health] - JsonWriter: sets application/health+json content type")]
    public async Task WriteAsync_ShouldSetHealthJsonContentType()
    {
        HealthReport report = ReportWith(HealthStatus.Healthy);
        TestHttpContext context = new("/healthz", HttpMethod.Get);

        await HealthCheckJsonResponseWriter.Instance.WriteAsync(context, report);

        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldContain("application/health+json");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - JsonWriter: serializes status, duration, tags, description and data")]
    public async Task WriteAsync_ShouldSerializeEntryShape()
    {
        var data = new Dictionary<string, object>
        {
            ["latencyMs"] = 42,
            ["region"] = "us-east",
            ["degraded"] = true,
        };
        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["db"] = new(HealthStatus.Degraded, "slow", TimeSpan.FromMilliseconds(12), exception: null, data, new[] { HealthTags.Ready, "sql" }),
        };
        var report = new HealthReport(entries, TimeSpan.FromMilliseconds(15));
        TestHttpContext context = new("/healthz", HttpMethod.Get);

        await HealthCheckJsonResponseWriter.Instance.WriteAsync(context, report);

        using JsonDocument document = JsonDocument.Parse(context.ReadResponseBody());
        JsonElement root = document.RootElement;
        root.GetProperty("status").GetString().ShouldBe("Degraded");

        JsonElement entry = root.GetProperty("entries").GetProperty("db");
        entry.GetProperty("status").GetString().ShouldBe("Degraded");
        entry.GetProperty("description").GetString().ShouldBe("slow");
        entry.GetProperty("durationMs").GetDouble().ShouldBeGreaterThan(0);

        JsonElement tags = entry.GetProperty("tags");
        tags.GetArrayLength().ShouldBe(2);

        JsonElement entryData = entry.GetProperty("data");
        entryData.GetProperty("latencyMs").GetInt32().ShouldBe(42);
        entryData.GetProperty("region").GetString().ShouldBe("us-east");
        entryData.GetProperty("degraded").GetBoolean().ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - JsonWriter: serializes a captured exception message")]
    public async Task WriteAsync_WhenEntryHasException_ShouldSerializeMessage()
    {
        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["db"] = new(HealthStatus.Unhealthy, "failed", TimeSpan.Zero, new InvalidOperationException("boom"), data: null, tags: null),
        };
        var report = new HealthReport(entries, TimeSpan.Zero);
        TestHttpContext context = new("/healthz", HttpMethod.Get);

        await HealthCheckJsonResponseWriter.Instance.WriteAsync(context, report);

        using JsonDocument document = JsonDocument.Parse(context.ReadResponseBody());
        document.RootElement
            .GetProperty("entries").GetProperty("db")
            .GetProperty("exception").GetString().ShouldBe("boom");
    }

    private static HealthReport ReportWith(HealthStatus status)
    {
        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["check"] = new(status, description: null, TimeSpan.Zero, exception: null, data: null, tags: null),
        };
        return new HealthReport(entries, TimeSpan.Zero);
    }
}
