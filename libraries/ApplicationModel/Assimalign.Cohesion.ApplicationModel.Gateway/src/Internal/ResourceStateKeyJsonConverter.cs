using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

[global::System.Diagnostics.DebuggerDisplay("ResourceStateKeyJsonConverter")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ResourceStateKeyJsonConverter : JsonConverter<ResourceStateKey>
{
    public override ResourceStateKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !ResourceStateKey.TryCreate(reader.GetString()!, out ResourceStateKey key))
        {
            throw new JsonException(ErrorMessages.ResourceStateKeyInvalid);
        }
        return key;
    }

    public override void Write(Utf8JsonWriter writer, ResourceStateKey value, JsonSerializerOptions options)
    {
        if (value.Value is null) { throw new JsonException(ErrorMessages.ResourceStateKeyInvalid); }
        writer.WriteStringValue(value.Value);
    }

    private sealed class DebugView
    {
        private readonly ResourceStateKeyJsonConverter _target;
        public DebugView(ResourceStateKeyJsonConverter value) => _target = value;
        public global::System.Type? Type => _target.Type;
        public bool HandleNull => _target.HandleNull;
    }
}
