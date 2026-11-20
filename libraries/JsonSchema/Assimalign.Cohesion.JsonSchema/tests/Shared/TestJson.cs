using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Tests;

/// <summary>
/// Parses JSON text into values that outlive the document they were read from.
/// </summary>
internal static class TestJson
{
    public static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Returns a JSON string value holding <paramref name="value"/>.</summary>
    public static JsonElement String(string value) => Parse("\"" + JsonEncodedText.Encode(value).ToString() + "\"");
}
