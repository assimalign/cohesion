using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

// A recorded snapshot in the record file: { "action": "cors", "value": "<base64>" }.
internal sealed class ApplicationGatewayStoreSnapshotJsonConverter : JsonConverter<ApplicationActionSnapshot>
{
    public override ApplicationActionSnapshot? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(ErrorMessages.ApplicationGatewayRecordSnapshotInvalid);
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
                    throw new JsonException(ErrorMessages.ApplicationGatewayRecordSnapshotInvalid);
                default:
                    reader.Skip();
                    break;
            }
        }

        if (action is null || value is null || value.Length == 0 || !ActionName.TryCreate(action, out ActionName name))
        {
            throw new JsonException(ErrorMessages.ApplicationGatewayRecordSnapshotInvalid);
        }

        return ApplicationActionSnapshot.Create(name, value);
    }

    public override void Write(Utf8JsonWriter writer, ApplicationActionSnapshot value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("action", value.Action.Value);
        writer.WriteBase64String("value", value.Value.Span);
        writer.WriteEndObject();
    }
}
