using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Converts an <see cref="ApplicationActionResult"/> to and from its JSON form: an object with the
/// result's <c>status</c> and, when set, its <c>output</c> as embedded JSON, its <c>reason</c>, and
/// its <c>state</c> snapshot.
/// </summary>
/// <remarks>
/// <code language="json">{ "status": "Succeeded", "state": { "action": "cors", "value": { "origins": [ "https://partner.example" ] } } }</code>
/// The output and the snapshot's value must be JSON, as <see cref="JsonApplicationContentSerializer"/>
/// writes them.
/// </remarks>
public sealed class ApplicationActionResultJsonConverter : JsonConverter<ApplicationActionResult>
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
    public override ApplicationActionResult? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(ErrorMessages.ApplicationActionResultInvalid);
        }

        string? status = null;
        byte[]? output = null;
        string? reason = null;
        ApplicationActionSnapshot? state = null;
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
                    output = ApplicationActionSnapshotJsonConverter.ReadRawValue(ref reader);
                    break;
                case "reason" when reader.TokenType is JsonTokenType.String or JsonTokenType.Null:
                    reason = reader.GetString();
                    break;
                case "state" when reader.TokenType == JsonTokenType.Null:
                    break;
                case "state":
                    state = ApplicationActionSnapshotJsonConverter.ReadSnapshot(ref reader);
                    break;
                case "status" or "reason":
                    throw new JsonException(ErrorMessages.ApplicationActionResultInvalid);
                default:
                    reader.Skip();
                    break;
            }
        }

        ApplicationActionStatus parsed = status switch
        {
            nameof(ApplicationActionStatus.Succeeded) => ApplicationActionStatus.Succeeded,
            nameof(ApplicationActionStatus.Rejected) => ApplicationActionStatus.Rejected,
            nameof(ApplicationActionStatus.NotFound) => ApplicationActionStatus.NotFound,
            nameof(ApplicationActionStatus.NotSupported) => ApplicationActionStatus.NotSupported,
            _ => throw new JsonException(ErrorMessages.ApplicationActionResultInvalid),
        };

        if (parsed == ApplicationActionStatus.Rejected && string.IsNullOrWhiteSpace(reason))
        {
            throw new JsonException(ErrorMessages.ApplicationActionResultInvalid);
        }

        return ApplicationActionResult.Create(parsed, output, reason, state);
    }

    /// <summary>
    /// Writes a result in its JSON form.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The result.</param>
    /// <param name="options">The serializer options.</param>
    /// <exception cref="ArgumentException">The result's output or state value is not JSON.</exception>
    public override void Write(Utf8JsonWriter writer, ApplicationActionResult value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteString("status", value.Status switch
        {
            ApplicationActionStatus.Succeeded => nameof(ApplicationActionStatus.Succeeded),
            ApplicationActionStatus.Rejected => nameof(ApplicationActionStatus.Rejected),
            ApplicationActionStatus.NotFound => nameof(ApplicationActionStatus.NotFound),
            _ => nameof(ApplicationActionStatus.NotSupported),
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
            ApplicationActionSnapshotJsonConverter.WriteSnapshot(writer, state);
        }

        writer.WriteEndObject();
    }
}
