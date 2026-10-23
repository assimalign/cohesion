using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.OpenTelemetry.Internal;

[global::System.Diagnostics.DebuggerDisplay("OtlpJsonContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ExportLogsServiceRequest))]
internal sealed partial class OtlpJsonContext : JsonSerializerContext
{
    private sealed class DebugView
    {
        private readonly OtlpJsonContext _target;

        public DebugView(OtlpJsonContext value)
        {
            _target = value;
        }

        public global::System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => _target.GeneratedSerializerOptions;
        public global::System.Text.Json.JsonSerializerOptions Options => _target.Options;
    }
}

internal sealed record ExportLogsServiceRequest(ResourceLogs[] ResourceLogs);
internal sealed record ResourceLogs(OtlpResource Resource, ScopeLogs[] ScopeLogs);
internal sealed record OtlpResource(KeyValue[] Attributes);
internal sealed record ScopeLogs(InstrumentationScope Scope, LogRecord[] LogRecords);
internal sealed record InstrumentationScope(string Name, string Version);
internal sealed record KeyValue(string Key, AnyValue Value);
internal sealed record LogRecord(string TimeUnixNano, string ObservedTimeUnixNano, int SeverityNumber,
    string SeverityText, AnyValue Body, KeyValue[] Attributes, string? TraceId, string? SpanId);
internal sealed class AnyValue
{
    public string? StringValue { get; init; }
    public bool? BoolValue { get; init; }
    public string? IntValue { get; init; }
    public double? DoubleValue { get; init; }
    public ArrayValue? ArrayValue { get; init; }
    public KeyValueList? KvlistValue { get; init; }
}
internal sealed record ArrayValue(AnyValue[] Values);
internal sealed record KeyValueList(KeyValue[] Values);
