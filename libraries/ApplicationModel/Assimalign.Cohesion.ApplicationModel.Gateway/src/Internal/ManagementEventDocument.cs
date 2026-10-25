using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

/// <summary>
/// Writes one management event as an OTLP/HTTP JSON logs request, the format a Cohesion telemetry sink ingests at
/// <c>/v1/logs</c>, so the application's telemetry is the management activity log (decision D8).
/// </summary>
/// <remarks>
/// The event names the operation, its target, the caller's subject and the generation; it never carries a
/// configuration value, an action input or a result, because those may be secrets.
/// </remarks>
internal static class ManagementEventDocument
{
    private const string scopeName = "Assimalign.Cohesion.ApplicationModel.Gateway";
    private const int informationSeverity = 9;

    public static byte[] Write(
        ResourceName gateway,
        ApplicationName application,
        ResourceName resource,
        string operation,
        string? target,
        string? subject,
        string? generation,
        string detail,
        DateTimeOffset time)
    {
        // A tick is 100 nanoseconds.
        string nanoseconds = ((time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100L).ToString(CultureInfo.InvariantCulture);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("resourceLogs");
            writer.WriteStartObject();
            writer.WriteStartObject("resource");
            writer.WriteStartArray("attributes");
            WriteAttribute(writer, "service.name", gateway.ToString());
            WriteAttribute(writer, "cohesion.application", application.ToString());
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteStartArray("scopeLogs");
            writer.WriteStartObject();
            writer.WriteStartObject("scope");
            writer.WriteString("name", scopeName);
            writer.WriteEndObject();
            writer.WriteStartArray("logRecords");
            writer.WriteStartObject();
            writer.WriteString("timeUnixNano", nanoseconds);
            writer.WriteString("observedTimeUnixNano", nanoseconds);
            writer.WriteNumber("severityNumber", informationSeverity);
            writer.WriteString("severityText", "INFO");
            writer.WriteStartObject("body");
            writer.WriteString("stringValue", detail);
            writer.WriteEndObject();
            writer.WriteStartArray("attributes");
            WriteAttribute(writer, "cohesion.resource", resource.ToString());
            WriteAttribute(writer, "cohesion.management.operation", operation);
            WriteAttribute(writer, "cohesion.management.target", target);
            WriteAttribute(writer, "cohesion.management.subject", subject);
            WriteAttribute(writer, "cohesion.management.generation", generation);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteAttribute(Utf8JsonWriter writer, string key, string? value)
    {
        if (value is null)
        {
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("key", key);
        writer.WriteStartObject("value");
        writer.WriteString("stringValue", value);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
