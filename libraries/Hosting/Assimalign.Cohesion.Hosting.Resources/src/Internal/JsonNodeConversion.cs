using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assimalign.Cohesion.Hosting.Resources.Internal;

/// <summary>Converts a mutable JSON node into a detached, immutable element without reflection.</summary>
internal static class JsonNodeConversion
{
    public static JsonElement ToElement(JsonNode node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            node.WriteTo(writer);
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
