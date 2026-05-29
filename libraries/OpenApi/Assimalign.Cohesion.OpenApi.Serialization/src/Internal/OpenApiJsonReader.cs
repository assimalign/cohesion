using System;
using System.IO;
using System.Text.Json;

namespace Assimalign.Cohesion.OpenApi.Serialization;

/// <summary>
/// The JSON implementation of <see cref="IOpenApiReader"/>. Parses JSON into the format-agnostic node
/// tree and then maps the tree into the canonical model.
/// </summary>
internal sealed class OpenApiJsonReader : IOpenApiReader
{
    public OpenApiDocument Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            using var json = JsonDocument.Parse(text);
            return Convert(json);
        }
        catch (JsonException exception)
        {
            throw new OpenApiException("The OpenAPI document is not valid JSON.", exception);
        }
    }

    public OpenApiDocument Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            using var json = JsonDocument.Parse(stream);
            return Convert(json);
        }
        catch (JsonException exception)
        {
            throw new OpenApiException("The OpenAPI document is not valid JSON.", exception);
        }
    }

    private static OpenApiDocument Convert(JsonDocument json)
    {
        if (OpenApiJsonNodeFormatter.Read(json.RootElement) is not OpenApiObjectNode root)
        {
            throw new OpenApiException("The root of an OpenAPI document must be a JSON object.");
        }

        return OpenApiNodeToModelConverter.Convert(root);
    }
}
