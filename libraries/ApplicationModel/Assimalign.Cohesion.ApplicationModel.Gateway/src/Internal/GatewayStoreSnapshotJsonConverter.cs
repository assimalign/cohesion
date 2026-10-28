using System;
using System.Text.Json;
using System.Text.Json.Serialization;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

// A recorded snapshot in the record file: { "action": "cors", "value": "<base64>" }.
internal sealed class GatewayStoreSnapshotJsonConverter : JsonConverter<ResourceStateSnapshot>
{
    public override ResourceStateSnapshot? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(ErrorMessages.GatewayRecordSnapshotInvalid);
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
                case "value" when reader.TokenType == JsonTokenType.String:
                    value = reader.GetBytesFromBase64();
                    break;
                case "action" or "value":
                    throw new JsonException(ErrorMessages.GatewayRecordSnapshotInvalid);
                default:
                    reader.Skip();
                    break;
            }
        }

        if (action is null || value is null || value.Length == 0 || !ResourceActionName.TryCreate(action, out ResourceActionName name))
        {
            throw new JsonException(ErrorMessages.GatewayRecordSnapshotInvalid);
        }

        return ResourceStateSnapshot.Create(name, value);
    }

    public override void Write(Utf8JsonWriter writer, ResourceStateSnapshot value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("action", value.Action.Value);
        writer.WriteBase64String("value", value.Value.Span);
        writer.WriteEndObject();
    }
}
