using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Hosting.Resources.Internal;

[global::System.Diagnostics.DebuggerDisplay("ResourceManagementPathJsonConverter")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ResourceManagementPathJsonConverter : JsonConverter<ResourceManagementPath>
{
    public override ResourceManagementPath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !ResourceManagementPath.TryCreate(reader.GetString()!, out ResourceManagementPath name))
        {
            throw new JsonException(ErrorMessages.ResourceManagementPathInvalid);
        }
        return name;
    }

    public override void Write(Utf8JsonWriter writer, ResourceManagementPath value, JsonSerializerOptions options)
    {
        if (value.Value is null) { throw new JsonException(ErrorMessages.ResourceManagementPathInvalid); }
        writer.WriteStringValue(value.Value);
    }

    private sealed class DebugView
    {
        private readonly ResourceManagementPathJsonConverter _target;
        public DebugView(ResourceManagementPathJsonConverter value) => _target = value;
        public global::System.Type? Type => _target.Type;
        public bool HandleNull => _target.HandleNull;
    }
}
