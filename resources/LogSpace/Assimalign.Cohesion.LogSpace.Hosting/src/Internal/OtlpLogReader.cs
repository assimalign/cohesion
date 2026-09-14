using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Assimalign.Cohesion.LogSpace.Hosting;

internal static class OtlpLogReader
{
    internal static List<StoredLog> Read(JsonElement root, string? authenticatedResource)
    {
        var result = new List<StoredLog>();
        if (!root.TryGetProperty("resourceLogs", out JsonElement groups)) { return result; }
        foreach (JsonElement resourceLogs in groups.EnumerateArray())
        {
            Dictionary<string, JsonElement> resourceAttributes = resourceLogs.TryGetProperty("resource", out JsonElement resource)
                ? ReadAttributes(resource) : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            string service = resourceAttributes.TryGetValue("service.name", out var name) ? name.ToString() : authenticatedResource ?? string.Empty;
            if (authenticatedResource is not null && service != authenticatedResource)
            { throw new UnauthorizedAccessException("service.name must match the authenticated emitting resource."); }
            if (!resourceLogs.TryGetProperty("scopeLogs", out JsonElement scopes)) { continue; }
            foreach (JsonElement scope in scopes.EnumerateArray())
            {
                string category = scope.TryGetProperty("scope", out var instrumentation) && instrumentation.TryGetProperty("name", out var categoryName)
                    ? categoryName.GetString() ?? string.Empty : string.Empty;
                if (!scope.TryGetProperty("logRecords", out JsonElement records)) { continue; }
                foreach (JsonElement record in records.EnumerateArray())
                {
                    if (result.Count == 8192) { throw new JsonException("At most 8192 records are accepted per request."); }
                    string time = record.TryGetProperty("timeUnixNano", out var timestamp) ? timestamp.ToString() : "0";
                    if (!ulong.TryParse(time, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    { throw new JsonException("Invalid timeUnixNano."); }
                    var attributes = new Dictionary<string, JsonElement>(resourceAttributes, StringComparer.Ordinal);
                    foreach (var attribute in ReadAttributes(record)) { attributes[attribute.Key] = attribute.Value; }
                    foreach (string id in new[] { "traceId", "spanId" })
                    {
                        if (record.TryGetProperty(id, out JsonElement value)) { attributes[id] = value.Clone(); }
                    }
                    result.Add(new StoredLog(time, record.TryGetProperty("severityNumber", out var severity) ? severity.GetInt32() : 0,
                        record.TryGetProperty("severityText", out var text) ? text.GetString() ?? string.Empty : string.Empty,
                        service, category, record.TryGetProperty("body", out var body) ? ReadValue(body).ToString() : string.Empty, attributes));
                }
            }
        }
        return result;
    }

    private static Dictionary<string, JsonElement> ReadAttributes(JsonElement owner)
    {
        var attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (owner.TryGetProperty("attributes", out JsonElement list))
        {
            foreach (JsonElement pair in list.EnumerateArray())
            { attributes[pair.GetProperty("key").GetString()!] = ReadValue(pair.GetProperty("value")); }
        }
        return attributes;
    }

    private static JsonElement ReadValue(JsonElement value)
    {
        var bytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bytes)) { WriteValue(writer, value); }
        using JsonDocument document = JsonDocument.Parse(bytes.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) { throw new JsonException("AnyValue must be an object."); }
        int count = 0;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (++count > 1) { throw new JsonException("AnyValue must carry exactly one value."); }
            switch (property.Name)
            {
                case "stringValue": writer.WriteStringValue(property.Value.GetString()); break;
                case "boolValue": writer.WriteBooleanValue(property.Value.GetBoolean()); break;
                case "intValue": writer.WriteNumberValue(long.Parse(property.Value.ToString(), CultureInfo.InvariantCulture)); break;
                case "doubleValue": writer.WriteNumberValue(property.Value.GetDouble()); break;
                case "arrayValue":
                    writer.WriteStartArray();
                    foreach (JsonElement item in property.Value.GetProperty("values").EnumerateArray()) { WriteValue(writer, item); }
                    writer.WriteEndArray(); break;
                case "kvlistValue":
                    writer.WriteStartObject();
                    foreach (JsonElement pair in property.Value.GetProperty("values").EnumerateArray())
                    { writer.WritePropertyName(pair.GetProperty("key").GetString()!); WriteValue(writer, pair.GetProperty("value")); }
                    writer.WriteEndObject(); break;
                default: throw new JsonException("Unsupported AnyValue variant.");
            }
        }
        if (count != 1) { throw new JsonException("AnyValue must carry exactly one value."); }
    }
}
