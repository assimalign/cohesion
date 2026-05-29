using System.Text.Json;

namespace Assimalign.Cohesion.OpenApi.Serialization;

/// <summary>
/// Renders the format-agnostic <see cref="OpenApiNode"/> tree to and from JSON text using
/// <see cref="Utf8JsonWriter"/> and <see cref="JsonDocument"/>. This is the only layer of the
/// serialization pipeline that is aware of JSON; the model-to-node mapping is format independent so a
/// YAML formatter can be added beside this one without touching it.
/// </summary>
internal static class OpenApiJsonNodeFormatter
{
    internal static void Write(Utf8JsonWriter writer, OpenApiNode node)
    {
        switch (node)
        {
            case OpenApiObjectNode obj:
                writer.WriteStartObject();
                foreach (var member in obj)
                {
                    writer.WritePropertyName(member.Key);
                    Write(writer, member.Value);
                }

                writer.WriteEndObject();
                break;

            case OpenApiArrayNode array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;

            case OpenApiValueNode value:
                WriteValue(writer, value);
                break;

            default:
                throw new OpenApiException($"Unsupported node type '{node.GetType().Name}'.");
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, OpenApiValueNode value)
    {
        switch (value.Kind)
        {
            case OpenApiValueKind.Null:
                writer.WriteNullValue();
                break;
            case OpenApiValueKind.Boolean:
                writer.WriteBooleanValue(value.GetBoolean());
                break;
            case OpenApiValueKind.Integer:
                writer.WriteNumberValue(value.GetInteger());
                break;
            case OpenApiValueKind.Double:
                writer.WriteNumberValue(value.GetDouble());
                break;
            case OpenApiValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            default:
                throw new OpenApiException($"Unsupported value kind '{value.Kind}'.");
        }
    }

    internal static OpenApiNode Read(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new OpenApiObjectNode();
                foreach (var property in element.EnumerateObject())
                {
                    obj[property.Name] = Read(property.Value);
                }

                return obj;

            case JsonValueKind.Array:
                var array = new OpenApiArrayNode();
                foreach (var item in element.EnumerateArray())
                {
                    array.Add(Read(item));
                }

                return array;

            case JsonValueKind.String:
                return OpenApiValueNode.String(element.GetString()!);

            case JsonValueKind.Number:
                return element.TryGetInt64(out var integer)
                    ? OpenApiValueNode.Integer(integer)
                    : OpenApiValueNode.Double(element.GetDouble());

            case JsonValueKind.True:
                return OpenApiValueNode.Boolean(true);

            case JsonValueKind.False:
                return OpenApiValueNode.Boolean(false);

            case JsonValueKind.Null:
                return OpenApiValueNode.Null;

            default:
                throw new OpenApiException($"Unsupported JSON value kind '{element.ValueKind}'.");
        }
    }
}
