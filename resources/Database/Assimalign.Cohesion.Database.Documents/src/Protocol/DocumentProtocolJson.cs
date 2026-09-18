using System;
using System.Text;
using System.Text.Json;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents;

internal static class DocumentProtocolJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void Validate(ReadOnlySpan<byte> json, bool requireObject = false)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(json);
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 256 });
            if (!reader.Read() || (requireObject && reader.TokenType != JsonTokenType.StartObject))
            {
                throw new ProtocolException("Expected a JSON parameter object or complete JSON result.");
            }
            reader.Skip();
            if (reader.Read())
            {
                throw new ProtocolException("A document payload must contain exactly one JSON value.");
            }
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            throw new ProtocolException("Malformed document JSON payload.", exception);
        }
    }
}
