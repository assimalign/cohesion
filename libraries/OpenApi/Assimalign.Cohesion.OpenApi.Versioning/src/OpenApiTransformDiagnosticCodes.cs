namespace Assimalign.Cohesion.OpenApi.Versioning;

/// <summary>
/// The stable diagnostic codes produced by <see cref="OpenApiVersionTransformer"/>.
/// </summary>
public static class OpenApiTransformDiagnosticCodes
{
    /// <summary>A schema <c>example</c> was moved into the <c>examples</c> array (3.0 → 3.1).</summary>
    public const string SchemaExampleConverted = "OPENAPIVER0001";

    /// <summary>A binary <c>format</c> was converted to a content keyword (3.0 → 3.1).</summary>
    public const string BinaryFormatConverted = "OPENAPIVER0002";

    /// <summary>A schema <c>const</c> was converted to a single-value <c>enum</c> (downgrade to 3.0).</summary>
    public const string ConstConverted = "OPENAPIVER0003";

    /// <summary>A multi-type schema was reduced to its first type (downgrade to 3.0).</summary>
    public const string MultiTypeReduced = "OPENAPIVER0004";

    /// <summary>Deprecated XML flags were converted to <c>nodeType</c> (3.1 → 3.2).</summary>
    public const string XmlNodeTypeConverted = "OPENAPIVER0005";

    /// <summary>A construct is not supported by the target version and is dropped when serialized.</summary>
    public const string UnsupportedConstruct = "OPENAPIVER0006";

    /// <summary>A schema <c>examples</c> array was reduced to a single <c>example</c> (downgrade to 3.0).</summary>
    public const string ExamplesConverted = "OPENAPIVER0007";
}
