using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Converts a <see cref="ResourceActionResult"/> to and from its JSON form: an object with the
/// result's <c>status</c> and, when set, its <c>output</c> as embedded JSON, its <c>reason</c>, and
/// its <c>state</c> snapshot.
/// </summary>
/// <remarks>
/// <code language="json">{ "status": "Succeeded", "state": { "action": "cors", "value": { "origins": [ "https://partner.example" ] } } }</code>
/// The output and the snapshot's value must be JSON, as <see cref="JsonResourceContentSerializer"/>
/// writes them.
/// </remarks>
public sealed class ResourceActionResultJsonConverter : JsonConverter<ResourceActionResult>
{
    /// <summary>
    /// Reads a result from its JSON form.
    /// </summary>
    /// <param name="reader">The reader, positioned on the result.</param>
    /// <param name="typeToConvert">The type to convert.</param>
    /// <param name="options">The serializer options.</param>
    /// <returns>The result, or <see langword="null"/> for the JSON literal <c>null</c>.</returns>
    /// <exception cref="JsonException">
    /// The JSON is not a result: it has no known status, a rejection has no reason, or its state is
    /// not a snapshot.
    /// </exception>
    public override ResourceActionResult? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(ErrorMessages.ResourceActionResultInvalid);
        }

        string? status = null;
        byte[]? output = null;
        string? reason = null;
        ResourceStateSnapshot? state = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string? property = reader.GetString();
            reader.Read();
            switch (property)
            {
                case "status" when reader.TokenType == JsonTokenType.String:
                    status = reader.GetString();
                    break;
                case "output":
                    output = ResourceStateSnapshotJsonConverter.ReadRawValue(ref reader);
                    break;
                case "reason" when reader.TokenType is JsonTokenType.String or JsonTokenType.Null:
                    reason = reader.GetString();
                    break;
                case "state" when reader.TokenType == JsonTokenType.Null:
                    break;
                case "state":
                    state = ResourceStateSnapshotJsonConverter.ReadSnapshot(ref reader);
                    break;
                case "status" or "reason":
                    throw new JsonException(ErrorMessages.ResourceActionResultInvalid);
                default:
                    reader.Skip();
                    break;
            }
        }

        ResourceActionStatus parsed = status switch
        {
            nameof(ResourceActionStatus.Succeeded) => ResourceActionStatus.Succeeded,
            nameof(ResourceActionStatus.Rejected) => ResourceActionStatus.Rejected,
            nameof(ResourceActionStatus.NotFound) => ResourceActionStatus.NotFound,
            nameof(ResourceActionStatus.NotSupported) => ResourceActionStatus.NotSupported,
            _ => throw new JsonException(ErrorMessages.ResourceActionResultInvalid),
        };

        if (parsed == ResourceActionStatus.Rejected && string.IsNullOrWhiteSpace(reason))
        {
            throw new JsonException(ErrorMessages.ResourceActionResultInvalid);
        }

        return ResourceActionResult.Create(parsed, output, reason, state);
    }

    /// <summary>
    /// Writes a result in its JSON form.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The result.</param>
    /// <param name="options">The serializer options.</param>
    /// <exception cref="ArgumentException">The result's output or state value is not JSON.</exception>
    public override void Write(Utf8JsonWriter writer, ResourceActionResult value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteString("status", value.Status switch
        {
            ResourceActionStatus.Succeeded => nameof(ResourceActionStatus.Succeeded),
            ResourceActionStatus.Rejected => nameof(ResourceActionStatus.Rejected),
            ResourceActionStatus.NotFound => nameof(ResourceActionStatus.NotFound),
            _ => nameof(ResourceActionStatus.NotSupported),
        });

        if (!value.Output.IsEmpty)
        {
            writer.WritePropertyName("output");
            writer.WriteRawValue(value.Output.Span);
        }

        if (value.Reason is not null)
        {
            writer.WriteString("reason", value.Reason);
        }

        if (value.State is { } state)
        {
            writer.WritePropertyName("state");
            ResourceStateSnapshotJsonConverter.WriteSnapshot(writer, state);
        }

        writer.WriteEndObject();
    }
}
