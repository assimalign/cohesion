using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

[global::System.Diagnostics.DebuggerDisplay("ResourceNameJsonConverter")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ResourceNameJsonConverter : JsonConverter<ResourceName>
{
    public override ResourceName Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.String)
        {
            throw new JsonException("A resource name must be a JSON string.");
        }

        string? value = reader.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException("A resource name cannot be empty.");
        }

        return value;
    }

    public override void Write(
        Utf8JsonWriter writer,
        ResourceName value,
        JsonSerializerOptions options)
    {
        string? text = value.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new JsonException("A resource name cannot be empty.");
        }

        writer.WriteStringValue(text);
    }

    private sealed class DebugView
    {
        private readonly ResourceNameJsonConverter _target;

        public DebugView(ResourceNameJsonConverter value)
        {
            _target = value;
        }

        public bool HandleNull => _target.HandleNull;
        public global::System.Type Type => _target.Type;
    }
}
