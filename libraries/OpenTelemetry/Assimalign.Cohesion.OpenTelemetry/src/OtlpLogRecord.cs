using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenTelemetry;

/// <summary>A transport-neutral OTLP log record.</summary>
/// <param name="Timestamp">Event time.</param>
/// <param name="SeverityNumber">OTLP severity number.</param>
/// <param name="SeverityText">Source severity name.</param>
/// <param name="Body">Rendered message.</param>
/// <param name="Category">Instrumentation scope name.</param>
/// <param name="Attributes">Additional structured values.</param>
/// <param name="TraceId">Optional 32-character hexadecimal trace identifier.</param>
/// <param name="SpanId">Optional 16-character hexadecimal span identifier.</param>
public sealed record OtlpLogRecord(DateTimeOffset Timestamp, int SeverityNumber, string SeverityText,
    string Body, string Category, IReadOnlyDictionary<string, object?>? Attributes, string? TraceId, string? SpanId);
