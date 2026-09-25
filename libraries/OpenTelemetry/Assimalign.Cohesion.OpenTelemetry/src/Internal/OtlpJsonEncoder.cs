using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Assimalign.Cohesion.OpenTelemetry.Internal;

internal static class OtlpJsonEncoder
{
    internal static byte[] Encode(ReadOnlyMemory<OtlpLogRecord> batch, IReadOnlyDictionary<string, string> attributes)
    {
        string observed = Nanoseconds(DateTimeOffset.UtcNow);
        ScopeLogs[] scopes = batch.ToArray().GroupBy(record => record.Category, StringComparer.Ordinal)
            .Select(group => new ScopeLogs(new InstrumentationScope(group.Key, "1"), group.Select(record =>
                new LogRecord(Nanoseconds(record.Timestamp), observed, record.SeverityNumber, record.SeverityText,
                    new AnyValue { StringValue = record.Body }, Map(record.Attributes),
                    Hex(record.TraceId, 32), Hex(record.SpanId, 16))).ToArray())).ToArray();
        var resource = new OtlpResource(attributes.Select(pair =>
            new KeyValue(pair.Key, new AnyValue { StringValue = pair.Value })).ToArray());
        return JsonSerializer.SerializeToUtf8Bytes(new ExportLogsServiceRequest([new ResourceLogs(resource, scopes)]),
            OtlpJsonContext.Default.ExportLogsServiceRequest);
    }

    private static string Nanoseconds(DateTimeOffset value)
    {
        decimal nanoseconds = (decimal)(value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100;
        if (nanoseconds < 0 || nanoseconds > ulong.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "OTLP timestamps must fit unsigned nanoseconds since the Unix epoch.");
        }
        return nanoseconds.ToString("0", CultureInfo.InvariantCulture);
    }

    private static string? Hex(string? value, int length)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length != length || !value.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("OTLP trace and span identifiers must be fixed-width hexadecimal.");
        }

        return value.ToLowerInvariant();
    }

    private static KeyValue[] Map(IReadOnlyDictionary<string, object?>? values, int depth = 0)
        => values?.Select(pair => new KeyValue(pair.Key, MapValue(pair.Value, depth + 1))).ToArray() ?? [];

    private static AnyValue MapValue(object? value, int depth)
    {
        if (depth > 16)
        {
            return new AnyValue { StringValue = "[depth limit]" };
        }

        switch (value)
        {
            case null: return new AnyValue { StringValue = "null" };
            case string text: return new AnyValue { StringValue = text };
            case bool boolean: return new AnyValue { BoolValue = boolean };
            case sbyte or byte or short or ushort or int or uint or long:
                return new AnyValue { IntValue = Convert.ToString(value, CultureInfo.InvariantCulture) };
            case ulong unsigned when unsigned <= long.MaxValue:
                return new AnyValue { IntValue = unsigned.ToString(CultureInfo.InvariantCulture) };
            case float single when float.IsFinite(single): return new AnyValue { DoubleValue = single };
            case double number when double.IsFinite(number): return new AnyValue { DoubleValue = number };
            case decimal number: return new AnyValue { DoubleValue = (double)number };
            case IReadOnlyDictionary<string, object?> dictionary:
                return new AnyValue { KvlistValue = new KeyValueList(Map(dictionary, depth)) };
            case IDictionary dictionary:
                var entries = new List<KeyValue>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    entries.Add(new KeyValue(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "null", MapValue(entry.Value, depth + 1)));
                }

                return new AnyValue { KvlistValue = new KeyValueList(entries.ToArray()) };
            case IEnumerable sequence:
                var items = new List<AnyValue>();
                foreach (object? item in sequence)
                {
                    if (items.Count == 2048)
                    {
                        break;
                    }

                    items.Add(MapValue(item, depth + 1));
                }
                return new AnyValue { ArrayValue = new ArrayValue(items.ToArray()) };
            default: return new AnyValue { StringValue = value.ToString() ?? "null" };
        }
    }
}
