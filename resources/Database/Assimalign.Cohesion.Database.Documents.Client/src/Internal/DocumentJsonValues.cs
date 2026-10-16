using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client.Internal;

internal static class DocumentJsonValues
{
    internal static byte[] Parameters(IReadOnlyDictionary<string, object?>? parameters)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = 256 });
        writer.WriteStartObject();
        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                writer.WritePropertyName(name);
                Write(writer, value);
            }
        }
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    internal static object? Read(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => json.GetString(),
        JsonValueKind.Number when json.TryGetDecimal(out var number) => number,
        JsonValueKind.Object or JsonValueKind.Array => json.Clone(),
        _ => throw new ProtocolException(ErrorMessages.JsonScalarInvalid),
    };

    private static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case bool boolean: writer.WriteBooleanValue(boolean); break;
            case string text: writer.WriteStringValue(text); break;
            case JsonElement json when json.ValueKind != JsonValueKind.Undefined:
                json.WriteTo(writer); break;
            case byte or sbyte or short or ushort or int or uint or long or ulong or decimal:
                writer.WriteNumberValue(Convert.ToDecimal(value, CultureInfo.InvariantCulture)); break;
            case float number when float.IsFinite(number): writer.WriteNumberValue(Convert.ToDecimal(number)); break;
            case double number when double.IsFinite(number): writer.WriteNumberValue(Convert.ToDecimal(number)); break;
            default: throw new ArgumentException(ErrorMessages.JsonScalarInvalid, nameof(value));
        }
    }
}
