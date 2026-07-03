using System;

using Assimalign.Cohesion.Content.Yaml;

namespace Assimalign.Cohesion.OpenApi.Serialization;

/// <summary>
/// Converts between the format-agnostic <see cref="OpenApiNode"/> tree and the YAML document model.
/// This is the only YAML-aware layer; all version logic stays in the model-to-node mapping, exactly
/// as it does for JSON.
/// </summary>
internal static class OpenApiYamlNodeFormatter
{
    internal static YamlNode Write(OpenApiNode node) => node switch
    {
        OpenApiObjectNode objectNode => WriteObject(objectNode),
        OpenApiArrayNode arrayNode => WriteArray(arrayNode),
        OpenApiValueNode valueNode => WriteValue(valueNode),
        _ => throw new OpenApiException($"Unknown node type '{node.GetType().Name}'.")
    };

    internal static OpenApiNode Read(YamlNode node) => node switch
    {
        YamlMapping mapping => ReadMapping(mapping),
        YamlSequence sequence => ReadSequence(sequence),
        YamlScalar scalar => ReadScalar(scalar),
        _ => throw new OpenApiException($"Unknown YAML node type '{node.GetType().Name}'.")
    };

    private static YamlMapping WriteObject(OpenApiObjectNode node)
    {
        var mapping = new YamlMapping();
        foreach (var member in node)
        {
            mapping.Add(member.Key, Write(member.Value));
        }

        return mapping;
    }

    private static YamlSequence WriteArray(OpenApiArrayNode node)
    {
        var sequence = new YamlSequence();
        foreach (var item in node)
        {
            sequence.Add(Write(item));
        }

        return sequence;
    }

    private static YamlScalar WriteValue(OpenApiValueNode node) => node.Kind switch
    {
        OpenApiValueKind.Null => YamlScalar.Null,
        OpenApiValueKind.Boolean => new YamlScalar(node.GetBoolean()),
        OpenApiValueKind.Integer => new YamlScalar(node.GetInteger()),
        OpenApiValueKind.Double => new YamlScalar(node.GetDouble()),
        OpenApiValueKind.String => YamlScalar.FromString(node.GetString()),
        _ => throw new OpenApiException($"Unknown value kind '{node.Kind}'.")
    };

    private static OpenApiObjectNode ReadMapping(YamlMapping mapping)
    {
        var node = new OpenApiObjectNode();
        foreach (var entry in mapping.Entries)
        {
            if (entry.Key is not YamlScalar key)
            {
                throw new OpenApiException("Mappings in an OpenAPI document must use scalar keys.");
            }

            node[key.Value] = Read(entry.Value);
        }

        return node;
    }

    private static OpenApiArrayNode ReadSequence(YamlSequence sequence)
    {
        var node = new OpenApiArrayNode();
        foreach (var item in sequence.Items)
        {
            node.Add(Read(item));
        }

        return node;
    }

    private static OpenApiNode ReadScalar(YamlScalar scalar) => scalar.Kind switch
    {
        YamlScalarKind.Null => OpenApiValueNode.Null,
        YamlScalarKind.Boolean => OpenApiValueNode.Boolean(scalar.GetBoolean()),
        YamlScalarKind.Integer => OpenApiValueNode.Integer(scalar.GetInteger()),
        YamlScalarKind.Float => OpenApiValueNode.Double(scalar.GetDouble()),
        YamlScalarKind.String => OpenApiValueNode.String(scalar.Value),
        _ => throw new OpenApiException($"Unknown scalar kind '{scalar.Kind}'.")
    };
}
