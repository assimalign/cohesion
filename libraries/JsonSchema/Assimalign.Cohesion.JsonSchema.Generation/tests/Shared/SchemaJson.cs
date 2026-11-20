using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

using Shouldly;

namespace Assimalign.Cohesion.JsonSchema.Generation.Tests;

/// <summary>
/// Reads generated documents: the value at a JSON Pointer, member names, and compact JSON text for
/// comparisons and failure messages.
/// </summary>
internal static class SchemaJson
{
    /// <summary>Returns the value at <paramref name="pointer"/> in the document's JSON.</summary>
    public static JsonElement At(JsonSchemaDocument document, string pointer)
    {
        JsonPointer location = new(pointer);
        location.TryEvaluate(document.Source, out JsonElement value).ShouldBeTrue("The document has no value at '" + pointer + "': " + Text(document.Source));
        return value;
    }

    /// <summary>Returns whether the document's JSON has a value at <paramref name="pointer"/>.</summary>
    public static bool Has(JsonSchemaDocument document, string pointer) =>
        new JsonPointer(pointer).TryEvaluate(document.Source, out _);

    /// <summary>Asserts that the value at <paramref name="pointer"/> equals the JSON text <paramref name="expected"/>.</summary>
    public static void ShouldHave(JsonSchemaDocument document, string pointer, string expected)
    {
        JsonElement actual = At(document, pointer);
        JsonElement wanted = Parse(expected);
        JsonElement.DeepEquals(actual, wanted).ShouldBeTrue("At '" + pointer + "' expected " + Text(wanted) + " but found " + Text(actual) + ".");
    }

    /// <summary>Returns the names of an object's members, in document order.</summary>
    public static List<string> Names(JsonElement value)
    {
        List<string> names = new();
        foreach (JsonProperty property in value.EnumerateObject())
        {
            names.Add(property.Name);
        }

        return names;
    }

    /// <summary>Parses JSON text into a value that outlives the document it was read from.</summary>
    public static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Returns the compact JSON text of a value.</summary>
    public static string Text(JsonElement value)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            value.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
