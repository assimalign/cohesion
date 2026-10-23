using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

[global::System.Diagnostics.DebuggerDisplay("ApplicationNameJsonConverter")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ApplicationNameJsonConverter : JsonConverter<ApplicationName>
{
    public override ApplicationName Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.String)
        {
            throw new JsonException(ErrorMessages.ApplicationNameJsonString);
        }

        string? value = reader.GetString();
        if (!ApplicationName.TryCreate(value, out ApplicationName name))
        {
            throw new JsonException(ErrorMessages.ApplicationNameInvalid);
        }

        return name;
    }

    public override void Write(
        Utf8JsonWriter writer,
        ApplicationName value,
        JsonSerializerOptions options)
    {
        string? text = value.ToString();
        if (!ApplicationName.TryCreate(text, out _))
        {
            throw new JsonException(ErrorMessages.ApplicationNameInvalid);
        }

        writer.WriteStringValue(text);
    }

    private sealed class DebugView
    {
        private readonly ApplicationNameJsonConverter _target;

        public DebugView(ApplicationNameJsonConverter value)
        {
            _target = value;
        }

        public bool HandleNull => _target.HandleNull;
        public global::System.Type Type => _target.Type;
    }
}
