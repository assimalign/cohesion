using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

[global::System.Diagnostics.DebuggerDisplay("ResourceStateNameJsonConverter")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ResourceStateNameJsonConverter : JsonConverter<ResourceStateName>
{
    public override ResourceStateName Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !ResourceStateName.TryCreate(reader.GetString()!, out ResourceStateName name))
        {
            throw new JsonException(ErrorMessages.ResourceStateNameInvalid);
        }
        return name;
    }

    public override void Write(Utf8JsonWriter writer, ResourceStateName value, JsonSerializerOptions options)
    {
        if (value.Value is null) { throw new JsonException(ErrorMessages.ResourceStateNameInvalid); }
        writer.WriteStringValue(value.Value);
    }

    private sealed class DebugView
    {
        private readonly ResourceStateNameJsonConverter _target;
        public DebugView(ResourceStateNameJsonConverter value) => _target = value;
        public global::System.Type? Type => _target.Type;
        public bool HandleNull => _target.HandleNull;
    }
}
