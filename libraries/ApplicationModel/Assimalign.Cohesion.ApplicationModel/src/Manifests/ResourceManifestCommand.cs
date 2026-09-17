using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Identifies a command kind accepted by a resource's default control plane.
/// </summary>
/// <param name="Kind">The stable command-kind identifier.</param>
[JsonConverter(typeof(ResourceManifestCommandJsonConverter))]
public sealed record ResourceManifestCommand(string Kind)
{
    internal sealed class ResourceManifestCommandJsonConverter : JsonConverter<ResourceManifestCommand>
    {
        public override ResourceManifestCommand Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("A resource manifest command must be a JSON string.");
            }

            string? kind = reader.GetString();
            if (string.IsNullOrWhiteSpace(kind))
            {
                throw new JsonException("A resource manifest command kind must not be empty.");
            }

            return new ResourceManifestCommand(kind);
        }

        public override void Write(
            Utf8JsonWriter writer,
            ResourceManifestCommand value,
            JsonSerializerOptions options)
        {
            if (string.IsNullOrWhiteSpace(value.Kind))
            {
                throw new JsonException("A resource manifest command kind must not be empty.");
            }

            writer.WriteStringValue(value.Kind);
        }
    }
}
