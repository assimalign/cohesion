using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// Reads and writes a <see cref="FabricValue{T}"/> of <see cref="string"/>: a JSON string is a
/// literal, and a <c>$ref</c> object is a <see cref="FabricRef"/>.
/// </summary>
/// <remarks>
/// A closed converter rather than a generic factory, so a source-generated
/// <see cref="JsonSerializerContext"/> uses it without reflection. A fabric area that needs
/// another literal type ships its own closed converter beside its spec.
/// </remarks>
public sealed class FabricStringValueJsonConverter : JsonConverter<FabricValue<string>>
{
    /// <summary>
    /// Reads a string literal or a <c>$ref</c> object.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <param name="typeToConvert">The type to convert.</param>
    /// <param name="options">The serializer options.</param>
    /// <returns>The value.</returns>
    /// <exception cref="JsonException">Thrown when the token is neither a string nor a well-formed <c>$ref</c> object.</exception>
    public override FabricValue<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return FabricValue<string>.FromLiteral(reader.GetString()!);
        }

        using var document = JsonDocument.ParseValue(ref reader);
        if (FabricRef.TryRead(document.RootElement, out var reference))
        {
            return FabricValue<string>.FromRef(reference);
        }

        throw new JsonException(ErrorMessages.FabricValueAmbiguous);
    }

    /// <summary>
    /// Writes the literal as a JSON string, or the reference as its <c>$ref</c> object.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">The serializer options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="writer"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    public override void Write(Utf8JsonWriter writer, FabricValue<string> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        if (value.IsRef)
        {
            value.Ref.WriteTo(writer);
            return;
        }

        writer.WriteStringValue(value.Literal);
    }
}
