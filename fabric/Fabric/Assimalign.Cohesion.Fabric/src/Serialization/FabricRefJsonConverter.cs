using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// Reads and writes a <see cref="FabricRef"/> as its <c>{ "$ref": ..., "path": ... }</c> object.
/// </summary>
public sealed class FabricRefJsonConverter : JsonConverter<FabricRef>
{
    /// <summary>
    /// Reads a <c>$ref</c> object.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <param name="typeToConvert">The type to convert.</param>
    /// <param name="options">The serializer options.</param>
    /// <returns>The reference.</returns>
    /// <exception cref="JsonException">Thrown when the value is not a well-formed <c>$ref</c> object.</exception>
    public override FabricRef? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (FabricRef.TryRead(document.RootElement, out var reference))
        {
            return reference;
        }

        throw new JsonException(ErrorMessages.FabricRefShapeInvalid);
    }

    /// <summary>
    /// Writes the reference's <c>$ref</c> object.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The reference.</param>
    /// <param name="options">The serializer options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="writer"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    public override void Write(Utf8JsonWriter writer, FabricRef value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.WriteTo(writer);
    }
}
