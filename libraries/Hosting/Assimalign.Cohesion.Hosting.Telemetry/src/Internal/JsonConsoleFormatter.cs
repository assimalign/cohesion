using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Hosting.Telemetry;

internal static class JsonConsoleFormatter
{
    internal static void Write(ILoggerEntry entry, TextWriter writer)
    {
        var attributes = new Dictionary<string, string>();
        foreach (var item in entry.Attributes)
        {
            attributes[item.Key] = item.Value?.ToString() ?? "null";
        }

        writer.WriteLine(JsonSerializer.Serialize(new ConsoleRecord(entry.Timestamp.ToString("O"), entry.Level.ToString(),
            entry.Category, entry.Message, entry.Exception?.ToString(), attributes), TelemetryJsonContext.Default.ConsoleRecord));
    }
}

internal sealed record ConsoleRecord(string Timestamp, string Level, string Category, string Message,
    string? Exception, Dictionary<string, string> Attributes);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConsoleRecord))]
internal sealed partial class TelemetryJsonContext : JsonSerializerContext;
