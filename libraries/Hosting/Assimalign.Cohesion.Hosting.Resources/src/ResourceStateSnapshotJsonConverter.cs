using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Converts a <see cref="ResourceStateSnapshot"/> to and from its JSON form: an object whose
/// <c>action</c> names the action and whose <c>value</c> is the state as embedded JSON.
/// </summary>
/// <remarks>
/// <code language="json">{ "action": "cors", "value": { "origins": [ "https://shop.example" ] } }</code>
/// The snapshot's value must be JSON, as <see cref="JsonResourceContentSerializer"/> writes it.
/// </remarks>
public sealed class ResourceStateSnapshotJsonConverter : JsonConverter<ResourceStateSnapshot>
{
    /// <summary>
    /// Reads a snapshot from its JSON form.
    /// </summary>
    /// <param name="reader">The reader, positioned on the snapshot.</param>
    /// <param name="typeToConvert">The type to convert.</param>
    /// <param name="options">The serializer options.</param>
    /// <returns>The snapshot, or <see langword="null"/> for the JSON literal <c>null</c>.</returns>
    /// <exception cref="JsonException">The JSON is not a snapshot, or its action name is invalid.</exception>
    public override ResourceStateSnapshot? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType == JsonTokenType.Null ? null : ReadSnapshot(ref reader);
    }

    /// <summary>
    /// Writes a snapshot in its JSON form.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The snapshot.</param>
    /// <param name="options">The serializer options.</param>
    /// <exception cref="ArgumentException">The snapshot's value is not JSON.</exception>
    public override void Write(Utf8JsonWriter writer, ResourceStateSnapshot value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        WriteSnapshot(writer, value);
    }

    internal static ResourceStateSnapshot ReadSnapshot(ref Utf8JsonReader reader)
    {
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
                    value = ReadRawValue(ref reader);
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

    internal static void WriteSnapshot(Utf8JsonWriter writer, ResourceStateSnapshot value)
    {
        writer.WriteStartObject();
        writer.WriteString("action", value.Action.Value);
        writer.WritePropertyName("value");
        writer.WriteRawValue(value.Value.Span);
        writer.WriteEndObject();
    }

    internal static byte[] ReadRawValue(ref Utf8JsonReader reader)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        return Encoding.UTF8.GetBytes(document.RootElement.GetRawText());
    }
}
