using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

internal sealed class ApplicationNameJsonConverter : JsonConverter<ApplicationName>
{
    public override ApplicationName Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.String)
        {
            throw new JsonException("An application name must be a JSON string.");
        }

        string? value = reader.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException("An application name cannot be empty.");
        }

        return value;
    }

    public override void Write(
        Utf8JsonWriter writer,
        ApplicationName value,
        JsonSerializerOptions options)
    {
        string? text = value.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new JsonException("An application name cannot be empty.");
        }

        writer.WriteStringValue(text);
    }
}
