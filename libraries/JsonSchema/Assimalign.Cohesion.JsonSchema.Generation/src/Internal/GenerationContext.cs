using System.Text.Json.Serialization.Metadata;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The context <see cref="JsonSchemaGenerationOptions.TransformSchemaNode"/> receives with one node.
/// </summary>
internal sealed class GenerationContext : IJsonSchemaGenerationContext
{
    public GenerationContext(JsonTypeInfo typeInfo, JsonPropertyInfo? propertyInfo, JsonTypeInfo? baseTypeInfo, bool isReference)
    {
        TypeInfo = typeInfo;
        PropertyInfo = propertyInfo;
        BaseTypeInfo = baseTypeInfo;
        IsReference = isReference;
    }

    public JsonTypeInfo TypeInfo { get; }

    public JsonPropertyInfo? PropertyInfo { get; }

    public JsonTypeInfo? BaseTypeInfo { get; }

    public bool IsReference { get; }
}
