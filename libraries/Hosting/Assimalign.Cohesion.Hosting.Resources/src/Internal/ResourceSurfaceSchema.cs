using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace Assimalign.Cohesion.Hosting.Resources.Internal;

/// <summary>
/// Produces runtime-surface schemas from source-generated metadata, annotated for a management UI.
/// </summary>
/// <remarks>
/// <see cref="DisplayNameAttribute"/> becomes <c>title</c> and <see cref="DescriptionAttribute"/> becomes
/// <c>description</c>, on the options type and on each of its properties, so a portal can render a form
/// from the schema alone.
/// </remarks>
internal static class ResourceSurfaceSchema
{
    private static readonly JsonSchemaExporterOptions _options = new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = Annotate,
    };

    public static JsonElement Create(JsonTypeInfo typeInfo) =>
        JsonNodeConversion.ToElement(typeInfo.GetJsonSchemaAsNode(_options));

    public static bool DescribesObject(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out JsonElement type))
        {
            return false;
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            return type.ValueEquals("object");
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement candidate in type.EnumerateArray())
            {
                if (candidate.ValueKind == JsonValueKind.String && candidate.ValueEquals("object"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static JsonNode Annotate(JsonSchemaExporterContext context, JsonNode schema)
    {
        if (schema is not JsonObject annotated)
        {
            return schema;
        }

        ICustomAttributeProvider? provider = context.PropertyInfo is { } property
            ? property.AttributeProvider
            : context.TypeInfo.Type;
        if (provider is null)
        {
            return schema;
        }

        foreach (object attribute in provider.GetCustomAttributes(inherit: true))
        {
            switch (attribute)
            {
                case DisplayNameAttribute { DisplayName.Length: > 0 } displayName when !annotated.ContainsKey("title"):
                    annotated["title"] = displayName.DisplayName;
                    break;
                case DescriptionAttribute { Description.Length: > 0 } description when !annotated.ContainsKey("description"):
                    annotated["description"] = description.Description;
                    break;
            }
        }

        return annotated;
    }
}
