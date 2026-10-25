using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assimalign.Cohesion.Hosting.Resources.Internal;

/// <summary>
/// Writes a value with ordinally sorted object members and no insignificant whitespace, so two values that
/// differ only in member order compare equal. Used to decide whether a section's effective value changed.
/// </summary>
internal static class CanonicalJson
{
    public static string Write(JsonNode? node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, node);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Write(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject value:
                writer.WriteStartObject();
                foreach (KeyValuePair<string, JsonNode?> member in value.OrderBy(static member => member.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(member.Key);
                    Write(writer, member.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonArray value:
                writer.WriteStartArray();
                foreach (JsonNode? item in value)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }
}
