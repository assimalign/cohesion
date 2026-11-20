namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// The JSON Schema specification releases whose core semantics the library implements.
/// </summary>
/// <remarks>
/// A dialect's version comes from its core vocabulary. It decides how identifiers, references and
/// array keywords behave: <c>id</c> or <c>$id</c>, whether <c>$ref</c> overrides its siblings, and
/// whether dynamic references use <c>$recursiveRef</c> or <c>$dynamicRef</c>.
/// </remarks>
public enum JsonSchemaVersion
{
    /// <summary>Draft 4 (draft-zyp-json-schema-04, draft-fge-json-schema-validation-00).</summary>
    Draft04 = 4,

    /// <summary>Draft 6 (draft-wright-json-schema-01).</summary>
    Draft06 = 6,

    /// <summary>Draft 7 (draft-handrews-json-schema-01).</summary>
    Draft07 = 7,

    /// <summary>Draft 2019-09 (draft-handrews-json-schema-02).</summary>
    Draft201909 = 2019,

    /// <summary>Draft 2020-12 (draft-bhutton-json-schema-01).</summary>
    Draft202012 = 2020,
}
