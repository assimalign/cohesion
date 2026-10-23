using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static class DocumentJsonValue
{
    internal static byte[] Encode(object? value, string? writeVerb = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = DocumentJsonEncoder.Instance, MaxDepth = 256 }))
        {
            Write(writer, value, writeVerb);
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal static JsonElement Object(IEnumerable<KeyValuePair<string, object?>> members, string? writeVerb = null, int maxDepth = 256)
    {
        // Couchbase expression/cons_object.go:53-77 constructs a keyed map and assigns
        // non-MISSING members into it (72-73). Later assignments replace the same key.
        var keyed = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (member.Value is not DocumentMissing) { keyed[member.Key] = member.Value; }
        }
        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = DocumentJsonEncoder.Instance, MaxDepth = writeVerb is not null ? 128 : maxDepth }))
            {
                writer.WriteStartObject();
                foreach (var member in keyed)
                {
                    writer.WritePropertyName(member.Key);
                    Write(writer, member.Value, writeVerb);
                }
                writer.WriteEndObject();
            }
            using var json = JsonDocument.Parse(buffer.WrittenMemory, new JsonDocumentOptions { MaxDepth = writeVerb is not null ? 128 : maxDepth });
            return json.RootElement.Clone();
        }
        catch (InvalidOperationException exception)
        {
            throw Depth("object", writeVerb, exception);
        }
    }

    internal static JsonElement Array(IEnumerable<object?> elements, string? writeVerb = null, int maxDepth = 256)
    {
        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = DocumentJsonEncoder.Instance, MaxDepth = writeVerb is not null ? 128 : maxDepth }))
            {
                writer.WriteStartArray();
                foreach (var element in elements) { Write(writer, element is DocumentMissing ? null : element, writeVerb); }
                writer.WriteEndArray();
            }
            using var json = JsonDocument.Parse(buffer.WrittenMemory, new JsonDocumentOptions { MaxDepth = writeVerb is not null ? 128 : maxDepth });
            return json.RootElement.Clone();
        }
        catch (InvalidOperationException exception)
        {
            throw Depth("array", writeVerb, exception);
        }
    }

    private static DocumentPlanningException Depth(string constructor, string? writeVerb, Exception exception)
        => writeVerb is not null
            ? new DocumentPlanningException("CD02008", string.Format(CultureInfo.CurrentCulture, ErrorMessages.WriteJsonInvalid, writeVerb), innerException: exception)
            : new DocumentPlanningException("CD05015", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.SelectConstructorDepthExceeded, constructor), innerException: exception);

    internal static void Write(Utf8JsonWriter writer, object? value, string? writeVerb = null)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case bool boolean: writer.WriteBooleanValue(boolean); break;
            case decimal number: writer.WriteNumberValue(number); break;
            case long number: writer.WriteNumberValue(number); break;
            case string text: writer.WriteStringValue(text); break;
            case JsonElement json: json.WriteTo(writer); break;
            default: throw new DocumentPlanningException("CD02008", writeVerb is null ? ErrorMessages.JsonScalarInvalid :
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.WriteValueMissing, writeVerb));
        }
    }
}
