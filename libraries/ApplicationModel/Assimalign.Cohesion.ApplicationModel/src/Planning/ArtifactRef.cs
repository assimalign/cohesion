using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Identifies the deployable artifact consumed by a plan without embedding a platform image
/// or executable reference in the platform-neutral contract.
/// </summary>
/// <param name="Value">The artifact reference's wire value.</param>
[JsonConverter(typeof(ArtifactRefJsonConverter))]
public readonly record struct ArtifactRef(string Value)
{
    /// <summary>
    /// Refers to the artifact produced for the resource described by the plan's manifest.
    /// This is the only artifact reference supported by <c>cohesion/plan/v1</c>.
    /// </summary>
    public static ArtifactRef Self { get; } = new("self");

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;

    internal sealed class ArtifactRefJsonConverter : JsonConverter<ArtifactRef>
    {
        public override ArtifactRef Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType is not JsonTokenType.String)
            {
                throw new JsonException("An artifact reference must be a JSON string.");
            }

            string? value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new JsonException("An artifact reference cannot be empty.");
            }

            return new ArtifactRef(value);
        }

        public override void Write(
            Utf8JsonWriter writer,
            ArtifactRef value,
            JsonSerializerOptions options)
        {
            if (string.IsNullOrWhiteSpace(value.Value))
            {
                throw new JsonException("An artifact reference cannot be empty.");
            }

            writer.WriteStringValue(value.Value);
        }
    }
}
