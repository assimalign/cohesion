using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Hosting.Resources.Internal;

[global::System.Diagnostics.DebuggerDisplay("ResourceManagementEndpointNameJsonConverter")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ResourceManagementEndpointNameJsonConverter : JsonConverter<ResourceManagementEndpointName>
{
    public override ResourceManagementEndpointName Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !ResourceManagementEndpointName.TryCreate(reader.GetString()!, out ResourceManagementEndpointName name))
        {
            throw new JsonException(ErrorMessages.ResourceManagementEndpointNameInvalid);
        }
        return name;
    }

    public override void Write(Utf8JsonWriter writer, ResourceManagementEndpointName value, JsonSerializerOptions options)
    {
        if (value.Value is null) { throw new JsonException(ErrorMessages.ResourceManagementEndpointNameInvalid); }
        writer.WriteStringValue(value.Value);
    }

    private sealed class DebugView
    {
        private readonly ResourceManagementEndpointNameJsonConverter _target;
        public DebugView(ResourceManagementEndpointNameJsonConverter value) => _target = value;
        public global::System.Type? Type => _target.Type;
        public bool HandleNull => _target.HandleNull;
    }
}
