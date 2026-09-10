using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

internal sealed class ControlPlaneSpecJsonConverter : JsonConverter<ControlPlaneSpec>
{
    public override bool HandleNull => true;

    public override ControlPlaneSpec Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.Null)
        {
            throw new JsonException("Resource plan controlPlane must not be null.");
        }

        if (reader.TokenType is not JsonTokenType.StartObject)
        {
            throw new JsonException("Resource plan controlPlane must be an object.");
        }

        string endpoint = string.Empty;
        string path = string.Empty;
        while (reader.Read() && reader.TokenType is not JsonTokenType.EndObject)
        {
            if (reader.TokenType is not JsonTokenType.PropertyName)
            {
                throw new JsonException("Resource plan controlPlane contains an invalid token.");
            }

            string propertyName = reader.GetString()
                ?? throw new JsonException("Resource plan controlPlane contains an invalid property name.");
            if (!reader.Read() || reader.TokenType is not JsonTokenType.String)
            {
                throw new JsonException(
                    $"Resource plan controlPlane property '{propertyName}' must be a string.");
            }

            string value = reader.GetString()
                ?? throw new JsonException(
                    $"Resource plan controlPlane property '{propertyName}' must not be null.");
            switch (propertyName)
            {
                case "endpoint":
                    endpoint = value;
                    break;
                case "path":
                    path = value;
                    break;
                default:
                    throw new JsonException(
                        $"Resource plan controlPlane property '{propertyName}' is not recognized.");
            }
        }

        if (reader.TokenType is not JsonTokenType.EndObject)
        {
            throw new JsonException("Resource plan controlPlane object was not terminated.");
        }

        return new ControlPlaneSpec(endpoint, path);
    }

    public override void Write(
        Utf8JsonWriter writer,
        ControlPlaneSpec value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteString("endpoint", value.Endpoint);
        writer.WriteString("path", value.Path);
        writer.WriteEndObject();
    }
}
