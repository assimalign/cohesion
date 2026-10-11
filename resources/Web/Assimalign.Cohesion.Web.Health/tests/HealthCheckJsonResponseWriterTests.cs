using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Health.Internal;

using Shouldly;
using Xunit;

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

    [Fact(DisplayName = "Cohesion Test [Web.Health] - JsonWriter: Should not publish a captured exception message by default")]
    public async Task WriteAsync_WhenEntryHasException_ShouldOmitExceptionMessage()
    {
        // Arrange
        HealthReport report = ReportWith("Primary database unavailable.", new InvalidOperationException(HostMessage));
        TestHttpContext context = new("/healthz", HttpMethod.Get);

        // Act
        await HealthCheckJsonResponseWriter.Instance.WriteAsync(context, report);

        // Assert
        string body = context.ReadResponseBody();
        body.ShouldNotContain(HostName, Case.Sensitive);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement entry = document.RootElement.GetProperty("entries").GetProperty("db");
        entry.TryGetProperty("exception", out _).ShouldBeFalse();
        entry.GetProperty("description").GetString().ShouldBe("Primary database unavailable.");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - JsonWriter: Should withhold a description equal to the exception message")]
    public async Task WriteAsync_WhenDescriptionIsExceptionMessage_ShouldWithholdDescription()
    {
        // Arrange: the HealthCheckResult.Unhealthy(exception.Message, exception) idiom.
        var exception = new InvalidOperationException(HostMessage);
        HealthReport report = ReportWith(exception.Message, exception);
        TestHttpContext context = new("/healthz", HttpMethod.Get);

        // Act
        await HealthCheckJsonResponseWriter.Instance.WriteAsync(context, report);

        // Assert
        string body = context.ReadResponseBody();
        body.ShouldNotContain(HostName, Case.Sensitive);
        using JsonDocument document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("entries").GetProperty("db")
            .GetProperty("description").GetString().ShouldBe(HealthCheckJsonResponseWriter.ReportedExceptionDescription);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - JsonWriter: Should withhold a description that embeds an inner exception message")]
    public async Task WriteAsync_WhenDescriptionEmbedsInnerExceptionMessage_ShouldWithholdDescription()
    {
        // Arrange
        var exception = new InvalidOperationException("Connection failed.", new System.IO.IOException(HostMessage));
        HealthReport report = ReportWith($"Database check failed: {HostMessage}", exception);
        TestHttpContext context = new("/healthz", HttpMethod.Get);

        // Act
        await HealthCheckJsonResponseWriter.Instance.WriteAsync(context, report);

        // Assert
        string body = context.ReadResponseBody();
        body.ShouldNotContain(HostName, Case.Sensitive);
        using JsonDocument document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("entries").GetProperty("db")
            .GetProperty("description").GetString().ShouldBe(HealthCheckJsonResponseWriter.ReportedExceptionDescription);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Health] - JsonWriter: Should publish the exception message and description with developer details")]
    public async Task WriteAsync_WithDeveloperDetails_ShouldPublishExceptionMessageAndDescription()
    {
        // Arrange
        var exception = new InvalidOperationException(HostMessage);
        HealthReport report = ReportWith(exception.Message, exception);
        TestHttpContext context = new("/healthz", HttpMethod.Get);

        // Act
        await HealthCheckJsonResponseWriter.DeveloperDetails.WriteAsync(context, report);

        // Assert
        using JsonDocument document = JsonDocument.Parse(context.ReadResponseBody());
        JsonElement entry = document.RootElement.GetProperty("entries").GetProperty("db");
        entry.GetProperty("exception").GetString().ShouldBe(HostMessage);
        entry.GetProperty("description").GetString().ShouldBe(HostMessage);
    }

    private const string HostName = "db-primary.corp.internal";

    private const string HostMessage = "Failed to connect to db-primary.corp.internal:5432.";

    private static HealthReport ReportWith(HealthStatus status)
    {
        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["check"] = new(status, description: null, TimeSpan.Zero, exception: null, data: null, tags: null),
        };
        return new HealthReport(entries, TimeSpan.Zero);
    }

    private static HealthReport ReportWith(string description, Exception exception)
    {
        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["db"] = new(HealthStatus.Unhealthy, description, TimeSpan.Zero, exception, data: null, tags: null),
        };
        return new HealthReport(entries, TimeSpan.Zero);
    }
}
