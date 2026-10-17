using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static class DocumentJsonValue
{
    internal static byte[] Encode(object? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = 128 }))
        {
            Write(writer, value);
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal static JsonElement Object(IEnumerable<KeyValuePair<string, object?>> members)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = 128 }))
        {
            writer.WriteStartObject();
            foreach (var member in members)
            {
                if (member.Value is DocumentMissing) { continue; }
                writer.WritePropertyName(member.Key);
                Write(writer, member.Value);
            }
            writer.WriteEndObject();
        }
        using var json = JsonDocument.Parse(buffer.WrittenMemory, new JsonDocumentOptions { MaxDepth = 128 });
        return json.RootElement.Clone();
    }

    private static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case bool boolean: writer.WriteBooleanValue(boolean); break;
            case decimal number: writer.WriteNumberValue(number); break;
            case string text: writer.WriteStringValue(text); break;
            case JsonElement json:
                try { json.WriteTo(writer); }
                catch (System.InvalidOperationException exception)
                {
                    throw new DocumentPlanningException("COHDOC008", ErrorMessages.InsertJsonInvalid, innerException: exception);
                }
                break;
            default: throw new DocumentPlanningException("COHDOC008", ErrorMessages.InsertValueMissing);
        }
    }
}
