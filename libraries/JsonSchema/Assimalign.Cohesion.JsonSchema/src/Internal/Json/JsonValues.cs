using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// Small helpers over <see cref="JsonElement"/>: code point lengths without materializing strings,
/// and standalone elements for computed annotation values.
/// </summary>
internal static class JsonValues
{
    public static readonly JsonElement True = Parse("true"u8);

    public static readonly JsonElement False = Parse("false"u8);

    /// <summary>
    /// Counts the Unicode code points of a string element (2020-12 Validation §6.3.1). An unescaped
    /// value is counted from its UTF-8 bytes without allocating.
    /// </summary>
    public static int CountCodePoints(JsonElement element)
    {
        ReadOnlySpan<byte> raw = JsonMarshal.GetRawUtf8Value(element);
        if (raw.Length >= 2 && raw[0] == '"')
        {
            raw = raw.Slice(1, raw.Length - 2);
        }

        if (raw.IndexOf((byte)'\\') < 0)
        {
            int count = 0;
            foreach (byte b in raw)
            {
                if ((b & 0xC0) != 0x80)
                {
                    count++;
                }
            }

            return count;
        }

        string value = element.GetString()!;
        int points = value.Length;
        for (int i = 0; i + 1 < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && char.IsLowSurrogate(value[i + 1]))
            {
                points--;
                i++;
            }
        }

        return points;
    }

    public static JsonElement CreateString(string value)
    {
        return Write(writer => writer.WriteStringValue(value));
    }

    public static JsonElement CreateNumber(long value)
    {
        return Write(writer => writer.WriteNumberValue(value));
    }

    public static JsonElement CreateStringArray(IEnumerable<string> values)
    {
        return Write(writer =>
        {
            writer.WriteStartArray();
            foreach (string value in values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        });
    }

    public static JsonElement CreateNumberArray(IEnumerable<int> values)
    {
        return Write(writer =>
        {
            writer.WriteStartArray();
            foreach (int value in values)
            {
                writer.WriteNumberValue(value);
            }

            writer.WriteEndArray();
        });
    }

    public static JsonElement Parse(ReadOnlySpan<byte> utf8)
    {
        Utf8JsonReader reader = new(utf8);
        return JsonElement.ParseValue(ref reader);
    }

    private static JsonElement Write(Action<Utf8JsonWriter> write)
    {
        ArrayBufferWriter<byte> buffer = new(64);
        using (Utf8JsonWriter writer = new(buffer))
        {
            write(writer);
        }

        return Parse(buffer.WrittenSpan);
    }
}
