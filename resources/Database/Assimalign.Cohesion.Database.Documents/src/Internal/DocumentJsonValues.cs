using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;


namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static class DocumentJsonValues
{
    internal static byte[] Result(IReadOnlyList<object?> values, bool wholeDocument)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = 256 });
        if (!wholeDocument) { writer.WriteStartArray(); }
        foreach (object? value in values) { Write(writer, value); }
        if (!wholeDocument) { writer.WriteEndArray(); }
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case bool boolean: writer.WriteBooleanValue(boolean); break;
            case string text: writer.WriteStringValue(text); break;
            case JsonElement json when json.ValueKind != JsonValueKind.Undefined:
                writer.WriteRawValue(json.GetRawText()); break;
            case byte or sbyte or short or ushort or int or uint or long or ulong or decimal:
                writer.WriteNumberValue(Convert.ToDecimal(value, CultureInfo.InvariantCulture)); break;
            case float number when float.IsFinite(number): writer.WriteNumberValue(Convert.ToDecimal(number)); break;
            case double number when double.IsFinite(number): writer.WriteNumberValue(Convert.ToDecimal(number)); break;
            default: throw new ArgumentException(ErrorMessages.JsonScalarInvalid, nameof(value));
        }
    }
}
