using System;
using System.IO;

using Assimalign.Cohesion.Content.Yaml;

namespace Assimalign.Cohesion.OpenApi.Serialization;

/// <summary>
/// The YAML implementation of <see cref="IOpenApiReader"/>. Parses YAML into the format-agnostic node
/// tree and then maps the tree into the canonical model, mirroring the JSON reader's shape so parse
/// failures surface consistently across formats.
/// </summary>
internal sealed class OpenApiYamlReader : IOpenApiReader
{
    public OpenApiDocument Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            return Convert(YamlText.Parse(text));
        }
        catch (YamlException exception)
        {
            throw new OpenApiException("The OpenAPI document is not valid YAML.", exception);
        }
    }

    public OpenApiDocument Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            return Convert(YamlText.Parse(stream));
        }
        catch (YamlException exception)
        {
            throw new OpenApiException("The OpenAPI document is not valid YAML.", exception);
        }
    }

    private static OpenApiDocument Convert(YamlStream stream)
    {
        if (stream.Count != 1)
        {
            throw new OpenApiException($"An OpenAPI description is a single YAML document, but the input contains {stream.Count}.");
        }

        if (stream[0].Root is not YamlMapping root)
        {
            throw new OpenApiException("The root of an OpenAPI document must be a YAML mapping.");
        }

        return OpenApiNodeToModelConverter.Convert((OpenApiObjectNode)OpenApiYamlNodeFormatter.Read(root));
    }
}
