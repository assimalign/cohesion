using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Identifies a command kind accepted by a resource's default control plane.
/// </summary>
/// <param name="Kind">The stable command-kind identifier.</param>
/// <remarks>
/// In <c>resource.json</c> a command is a JSON string holding its kind, or, when
/// <see cref="RequiresInputResolver"/> is set, an object
/// <c>{ "kind": "...", "requiresInputResolver": true }</c>. Only a set flag changes the written form,
/// so a manifest whose commands declare no requirement serializes exactly as before.
/// </remarks>
[JsonConverter(typeof(ResourceManifestCommandJsonConverter))]
public sealed record ResourceManifestCommand(string Kind)
{
    private const string kindProperty = "kind";
    private const string requiresInputResolverProperty = "requiresInputResolver";

    /// <summary>
    /// Gets a value indicating whether the gateway must rewrite this command's declared payload
    /// through a registered <see cref="IResourceCommandInputResolver"/> before delivering it, because
    /// the resource accepts only the resolved form (a declared source expression replaced by the
    /// value it names).
    /// </summary>
    /// <remarks>
    /// The resource that accepts the command declares the requirement; the declaring application
    /// meets it by registering a resolver for <see cref="Kind"/> in
    /// <see cref="ApplicationProviders.CommandInputs"/>, usually through its area's
    /// <c>Use&lt;Area&gt;(...)</c> verb. <see cref="IApplicationBuilder.Build"/>, and an application set
    /// when it starts a member, fail when an application declares such a command without that
    /// registration, instead of letting the resource reject the unresolved payload at delivery.
    /// </remarks>
    public bool RequiresInputResolver { get; init; }

    internal sealed class ResourceManifestCommandJsonConverter : JsonConverter<ResourceManifestCommand>
    {
        public override ResourceManifestCommand Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return new ResourceManifestCommand(ReadKind(ref reader));
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException(
                    "A resource manifest command must be a JSON string or an object with a 'kind' property.");
            }

            string? kind = null;
            bool requiresInputResolver = false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException("A resource manifest command object is malformed.");
                }

                string? property = reader.GetString();
                reader.Read();
                switch (property)
                {
                    case kindProperty when reader.TokenType == JsonTokenType.String:
                        kind = ReadKind(ref reader);
                        break;
                    case requiresInputResolverProperty when reader.TokenType is JsonTokenType.True or JsonTokenType.False:
                        requiresInputResolver = reader.GetBoolean();
                        break;
                    case kindProperty:
                        throw new JsonException("A resource manifest command 'kind' must be a JSON string.");
                    case requiresInputResolverProperty:
                        throw new JsonException(
                            "A resource manifest command 'requiresInputResolver' must be true or false.");
                    default:
                        throw new JsonException(
                            $"A resource manifest command has unknown property '{property}'; only " +
                            $"'{kindProperty}' and '{requiresInputResolverProperty}' are defined.");
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject)
            {
                throw new JsonException("A resource manifest command object is not terminated.");
            }

            if (kind is null)
            {
                throw new JsonException("A resource manifest command object requires a 'kind' property.");
            }

            return new ResourceManifestCommand(kind) { RequiresInputResolver = requiresInputResolver };
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

            if (!value.RequiresInputResolver)
            {
                writer.WriteStringValue(value.Kind);
                return;
            }

            writer.WriteStartObject();
            writer.WriteString(kindProperty, value.Kind);
            writer.WriteBoolean(requiresInputResolverProperty, true);
            writer.WriteEndObject();
        }

        private static string ReadKind(ref Utf8JsonReader reader)
        {
            string? kind = reader.GetString();
            if (string.IsNullOrWhiteSpace(kind))
            {
                throw new JsonException("A resource manifest command kind must not be empty.");
            }

            return kind;
        }
    }
}
