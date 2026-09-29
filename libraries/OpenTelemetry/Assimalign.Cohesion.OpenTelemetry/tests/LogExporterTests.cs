using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.OpenTelemetry.Tests;

public sealed class LogExporterTests
{
    [Fact(DisplayName = "Cohesion Test [OpenTelemetry] - JSON: preserves protobuf mapping and category scopes")]
    public async Task ExportAsync_WithStructuredRecords_ShouldWriteOtlpJson()
    {
        var handler = new CaptureHandler();
        var options = Options(handler);
        options.Headers = new Dictionary<string, string> { ["X-Example"] = "header-value" };
        options.ResourceAttributes = new Dictionary<string, string> { ["service.name"] = "api" };
        await using IOtlpLogExporter exporter = OtlpExporter.CreateLogExporter(options);
        var attributes = new Dictionary<string, object?>
        {
            ["text"] = "hello", ["flag"] = true, ["integer"] = long.MaxValue, ["number"] = 1.25,
            ["array"] = new object?[] { "item", false }, ["map"] = new Dictionary<string, object?> { ["key"] = 42 }, ["nil"] = null
        };
        var record = new OtlpLogRecord(DateTimeOffset.UnixEpoch.AddTicks(123), 9, "INFO", "hello", "orders", attributes,
            "ABCDEF0123456789ABCDEF0123456789", "ABCDEF0123456789");
        (await exporter.ExportAsync(new[] { record, record with { Category = "payments", TraceId = null, SpanId = null } }, CancellationToken.None)).ShouldBeTrue();
        handler.Path.ShouldBe("/collector/v1/logs");
        handler.ContentType.ShouldBe("application/json");
        handler.Header.ShouldBe("header-value");
        using JsonDocument document = JsonDocument.Parse(handler.Bodies.Single());
        JsonElement root = document.RootElement;
        root.EnumerateObject().Select(property => property.Name).ShouldBe(new[] { "resourceLogs" });
        JsonElement resource = root.GetProperty("resourceLogs")[0];
        resource.GetProperty("resource").GetProperty("attributes")[0].GetProperty("value").GetProperty("stringValue").GetString().ShouldBe("api");
        JsonElement scopes = resource.GetProperty("scopeLogs");
        scopes.GetArrayLength().ShouldBe(2);
        scopes[0].GetProperty("scope").GetProperty("name").GetString().ShouldBe("orders");
        JsonElement log = scopes[0].GetProperty("logRecords")[0];
        log.GetProperty("timeUnixNano").GetString().ShouldBe("12300");
        log.GetProperty("observedTimeUnixNano").ValueKind.ShouldBe(JsonValueKind.String);
        log.GetProperty("traceId").GetString().ShouldBe("abcdef0123456789abcdef0123456789");
        log.GetProperty("spanId").GetString().ShouldBe("abcdef0123456789");
        scopes[1].GetProperty("logRecords")[0].TryGetProperty("traceId", out _).ShouldBeFalse();
        var values = log.GetProperty("attributes").EnumerateArray().ToDictionary(pair => pair.GetProperty("key").GetString()!, pair => pair.GetProperty("value"));
        values["text"].GetRawText().ShouldBe("{\"stringValue\":\"hello\"}");
        values["flag"].GetRawText().ShouldBe("{\"boolValue\":true}");
        values["integer"].GetRawText().ShouldBe("{\"intValue\":\"9223372036854775807\"}");
        values["number"].GetRawText().ShouldBe("{\"doubleValue\":1.25}");
        values["array"].GetRawText().ShouldBe("{\"arrayValue\":{\"values\":[{\"stringValue\":\"item\"},{\"boolValue\":false}]}}");
        values["map"].GetRawText().ShouldBe("{\"kvlistValue\":{\"values\":[{\"key\":\"key\",\"value\":{\"intValue\":\"42\"}}]}}");
        values["nil"].GetRawText().ShouldBe("{\"stringValue\":\"null\"}");
    }

    [Fact(DisplayName = "Cohesion Test [OpenTelemetry] - Queue: overflow drops oldest and disposal flushes")]
    public async Task TryEnqueue_WhenFull_ShouldDropOldestAndCount()
    {
        var handler = new CaptureHandler();
        var options = Options(handler); options.MaxQueueLength = 2; options.MaxBatchSize = 1;
        IOtlpLogExporter exporter = OtlpExporter.CreateLogExporter(options);
        exporter.TryEnqueue(Record("first")).ShouldBeTrue();
        exporter.TryEnqueue(Record("second")).ShouldBeTrue();
        exporter.TryEnqueue(Record("third")).ShouldBeTrue();
        exporter.DroppedCount.ShouldBe(1);
        await exporter.DisposeAsync();
        handler.Bodies.Count.ShouldBe(2);
        string.Join("", handler.Bodies).ShouldNotContain("first", Case.Sensitive);
        exporter.TryEnqueue(Record("late")).ShouldBeFalse();
        exporter.DroppedCount.ShouldBe(2);
        await exporter.DisposeAsync();
    }

    [Theory(DisplayName = "Cohesion Test [OpenTelemetry] - HTTP: retries transient errors and drops permanent failures")]
    [InlineData(400, 1)]
    [InlineData(503, 3)]
    [InlineData(429, 3)]
    public async Task ExportAsync_WithFailureStatus_ShouldBoundRetries(int status, int expected)
    {
        var handler = new CaptureHandler { Status = (HttpStatusCode)status };
        await using IOtlpLogExporter exporter = OtlpExporter.CreateLogExporter(Options(handler));
        (await exporter.ExportAsync(new[] { Record("message") }, CancellationToken.None)).ShouldBeFalse();
        handler.Bodies.Count.ShouldBe(expected);
        exporter.FailedExportCount.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [OpenTelemetry] - Retry-After: server delay is respected")]
    public async Task ExportAsync_WithRetryAfter_ShouldWaitBeforeRetry()
    {
        var handler = new CaptureHandler { Status = HttpStatusCode.ServiceUnavailable, RetryAfter = TimeSpan.FromMilliseconds(150), SucceedAfter = 1 };
        await using IOtlpLogExporter exporter = OtlpExporter.CreateLogExporter(Options(handler));
        Stopwatch elapsed = Stopwatch.StartNew();
        (await exporter.ExportAsync(new[] { Record("message") }, CancellationToken.None)).ShouldBeTrue();
        elapsed.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(140));
        handler.Bodies.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [OpenTelemetry] - Network: unreachable collector and cancellation never throw")]
    public async Task ExportAsync_Unreachable_ShouldIsolateFailure()
    {
        var handler = new CaptureHandler { FailNetwork = true };
        await using IOtlpLogExporter exporter = OtlpExporter.CreateLogExporter(Options(handler));
        (await exporter.ExportAsync(new[] { Record("message") }, CancellationToken.None)).ShouldBeFalse();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await exporter.FlushAsync(cancelled.Token);
        (await exporter.ExportAsync(new[] { Record("message") }, cancelled.Token)).ShouldBeFalse();
        exporter.FailedExportCount.ShouldBeGreaterThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [OpenTelemetry] - HTTP: partial rejection is counted without retry")]
    public async Task ExportAsync_PartialRejection_ShouldReturnFalse()
    {
        var handler = new CaptureHandler { Response = "{\"partialSuccess\":{\"rejectedLogRecords\":\"1\",\"errorMessage\":\"full\"}}" };
        await using IOtlpLogExporter exporter = OtlpExporter.CreateLogExporter(Options(handler));
        (await exporter.ExportAsync(new[] { Record("message") }, CancellationToken.None)).ShouldBeFalse();
        handler.Bodies.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [OpenTelemetry] - Timeout: a stalled attempt retries within the total budget")]
    public async Task ExportAsync_StalledAttempt_ShouldRetry()
    {
        var handler = new CaptureHandler { StallFirst = true };
        await using IOtlpLogExporter exporter = OtlpExporter.CreateLogExporter(Options(handler));
        (await exporter.ExportAsync(new[] { Record("message") })).ShouldBeTrue();
        handler.Bodies.Count.ShouldBe(2);
        exporter.FailedExportCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [OpenTelemetry] - Response limit: oversized acknowledgement is discarded without retry")]
    public async Task ExportAsync_OversizedAcknowledgement_ShouldNotRetry()
    {
        var handler = new CaptureHandler { Response = new string('x', 65537) };
        await using IOtlpLogExporter exporter = OtlpExporter.CreateLogExporter(Options(handler));
        (await exporter.ExportAsync(new[] { Record("message") })).ShouldBeFalse();
        handler.Bodies.Count.ShouldBe(1);
    }

    private static OtlpLogRecord Record(string body) => new(DateTimeOffset.UtcNow, 9, "INFO", body, "test", null, null, null);
    private static OtlpExporterOptions Options(CaptureHandler handler) => new()
    { Endpoint = new Uri("https://collector.test/collector"), HandlerFactory = () => handler, FlushInterval = TimeSpan.FromDays(1), Timeout = TimeSpan.FromSeconds(2) };

    private sealed class CaptureHandler : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        internal string? Path { get; private set; }
        internal string? ContentType { get; private set; }
        internal string? Header { get; private set; }
        internal HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        internal TimeSpan? RetryAfter { get; init; }
        internal int SucceedAfter { get; init; } = int.MaxValue;
        internal bool FailNetwork { get; init; }
        internal bool StallFirst { get; init; }
        internal string Response { get; init; } = "{\"partialSuccess\":{}}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailNetwork) { throw new HttpRequestException("unreachable"); }
            Path = request.RequestUri!.AbsolutePath;
            ContentType = request.Content!.Headers.ContentType!.MediaType;
            Header = request.Headers.TryGetValues("X-Example", out var values) ? values.Single() : null;
            Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            if (StallFirst && Bodies.Count == 1) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            var response = new HttpResponseMessage(Bodies.Count > SucceedAfter ? HttpStatusCode.OK : Status) { Content = new StringContent(Response) };
            if (RetryAfter is { } delay) { response.Headers.RetryAfter = new RetryConditionHeaderValue(delay); }
            return response;
        }
    }
}
