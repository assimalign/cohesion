using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>Reflection-free JSON pretty printing for the document editor.</summary>
internal static class JsonText
{
    public static string Pretty(ReadOnlySpan<byte> utf8)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8.ToArray());
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                document.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            // Not JSON after all: show the stored bytes as they are.
            return Bytes.ToDisplay(utf8);
        }
    }
}
