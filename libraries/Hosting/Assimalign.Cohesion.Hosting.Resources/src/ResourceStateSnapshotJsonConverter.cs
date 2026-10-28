using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Converts a <see cref="ResourceStateSnapshot"/> to and from its wire form: an object whose
/// <c>action</c> names the action and whose <c>value</c> is the state as embedded JSON.
/// </summary>
/// <remarks>
/// <code language="json">{ "action": "cors", "value": { "origins": [ "https://shop.example" ] } }</code>
/// </remarks>
public sealed class ResourceStateSnapshotJsonConverter : JsonConverter<ResourceStateSnapshot>
{
    /// <summary>
    /// Reads a snapshot from its wire form.
    /// </summary>
    /// <param name="reader">The reader, positioned on the snapshot.</param>
    /// <param name="typeToConvert">The type to convert.</param>
    /// <param name="options">The serializer options.</param>
    /// <returns>The snapshot, or <see langword="null"/> for the JSON literal <c>null</c>.</returns>
    /// <exception cref="JsonException">The JSON is not a snapshot, or its action name is invalid.</exception>
    public override ResourceStateSnapshot? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(ErrorMessages.ResourceStateSnapshotInvalid);
        }

        string? action = null;
        byte[]? value = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string? property = reader.GetString();
            reader.Read();
            switch (property)
            {
                case "action" when reader.TokenType == JsonTokenType.String:
                    action = reader.GetString();
                    break;
                case "value":
                    using (JsonDocument document = JsonDocument.ParseValue(ref reader))
                    {
                        value = Encoding.UTF8.GetBytes(document.RootElement.GetRawText());
                    }
                    break;
                case "action":
                    throw new JsonException(ErrorMessages.ResourceStateSnapshotInvalid);
                default:
                    reader.Skip();
                    break;
            }
        }

        if (action is null || value is null)
        {
            throw new JsonException(ErrorMessages.ResourceStateSnapshotInvalid);
        }

        if (!ResourceActionName.TryCreate(action, out ResourceActionName name))
        {
            throw new JsonException(ErrorMessages.ResourceActionNameInvalid);
        }

        return ResourceStateSnapshot.Create(name, value);
    }

    /// <summary>
    /// Writes a snapshot in its wire form.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The snapshot.</param>
    /// <param name="options">The serializer options.</param>
    public override void Write(Utf8JsonWriter writer, ResourceStateSnapshot value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteString("action", value.Action.Value);
        writer.WritePropertyName("value");
        writer.WriteRawValue(value.Value.Span);
        writer.WriteEndObject();
    }
}
