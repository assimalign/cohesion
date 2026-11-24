namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// Where a keyword's value holds subschemas, so the loader can compile them, register the identifiers
/// and anchors they declare, and let the keyword apply them.
/// </summary>
public enum JsonSchemaSubschemaLayout
{
    /// <summary>The value holds no subschemas.</summary>
    None,

    /// <summary>The value is one schema, as for <c>not</c>.</summary>
    Schema,

    /// <summary>The value is an array of schemas, as for <c>allOf</c>.</summary>
    SchemaArray,

    /// <summary>The value is an object whose member values are schemas, as for <c>properties</c>.</summary>
    SchemaMap,

    /// <summary>The value is one schema or an array of schemas, as for draft 2019-09 <c>items</c>.</summary>
    SchemaOrSchemaArray,
}
