using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Health.Internal;

using Assimalign.Cohesion.Http;

/// <summary>
/// The default <see cref="IHealthResponseWriter"/>. Emits the health report as
/// <c>application/health+json</c> using a hand-written <see cref="Utf8JsonWriter"/>.
/// </summary>
/// <remarks>
/// Serialization is fully hand-written — no reflection-based <c>JsonSerializer</c> — so the
/// writer is trim- and NativeAOT-safe (mirrors the <c>OpenApiJsonWriter</c> precedent). Diagnostic
/// <see cref="HealthReportEntry.Data"/> values are written through a closed type switch; anything
/// outside the known primitive set falls back to its <see cref="object.ToString"/> form rather
/// than pulling in a runtime serializer.
/// </remarks>
internal sealed class HealthCheckJsonResponseWriter : IHealthResponseWriter
{
    /// <summary>
    /// The shared, stateless default instance.
    /// </summary>
    public static readonly HealthCheckJsonResponseWriter Instance = new();

    private const string HealthJsonContentType = "application/health+json; charset=utf-8";

    public async ValueTask WriteAsync(IHttpContext context, HealthReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        context.Response.Headers[HttpHeaderKey.ContentType] = HealthJsonContentType;

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteReport(writer, report);
        }

        await context.Response.Body.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteReport(Utf8JsonWriter writer, HealthReport report)
    {
        writer.WriteStartObject();
        writer.WriteString("status", ToStatusString(report.Status));
        writer.WriteNumber("totalDurationMs", report.TotalDuration.TotalMilliseconds);

        writer.WriteStartObject("entries");
        foreach (KeyValuePair<string, HealthReportEntry> entry in report.Entries)
        {
            writer.WritePropertyName(entry.Key);
            WriteEntry(writer, entry.Value);
        }
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void WriteEntry(Utf8JsonWriter writer, HealthReportEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteString("status", ToStatusString(entry.Status));
        writer.WriteNumber("durationMs", entry.Duration.TotalMilliseconds);

        if (entry.Description is not null)
        {
            writer.WriteString("description", entry.Description);
        }

        if (entry.Exception is not null)
        {
            writer.WriteString("exception", entry.Exception.Message);
        }

        writer.WriteStartArray("tags");
        foreach (string tag in entry.Tags)
        {
            writer.WriteStringValue(tag);
        }
        writer.WriteEndArray();

        if (entry.Data.Count > 0)
        {
            writer.WriteStartObject("data");
            foreach (KeyValuePair<string, object> item in entry.Data)
            {
                writer.WritePropertyName(item.Key);
                WriteDataValue(writer, item.Value);
            }
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteDataValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            default:
                // Closed serialization: no reflection fallback. Anything exotic is stringified.
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static string ToStatusString(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "Healthy",
        HealthStatus.Degraded => "Degraded",
        HealthStatus.Unhealthy => "Unhealthy",
        _ => "Unhealthy",
    };
}
