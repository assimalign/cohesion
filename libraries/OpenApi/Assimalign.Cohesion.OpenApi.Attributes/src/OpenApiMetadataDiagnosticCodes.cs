namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// The stable diagnostic codes produced by <see cref="OpenApiAttributeMapper"/>. The source generator
/// reuses these codes so runtime mapping and compile-time generation agree on rule identity.
/// </summary>
public static class OpenApiMetadataDiagnosticCodes
{
    /// <summary>An operation attribute declares an empty path.</summary>
    public const string MissingPath = "OPENAPIATTR0001";

    /// <summary>A request body or response declares both a model type and an explicit schema reference.</summary>
    public const string AmbiguousSchema = "OPENAPIATTR0002";

    /// <summary>A path parameter was not marked required and was corrected to required.</summary>
    public const string PathParameterRequired = "OPENAPIATTR0003";

    /// <summary>An example declares both an embedded value and an external value.</summary>
    public const string AmbiguousExample = "OPENAPIATTR0004";

    /// <summary>An example declares neither an embedded value nor an external value.</summary>
    public const string EmptyExample = "OPENAPIATTR0005";

    /// <summary>An API key security scheme is missing its parameter name or location.</summary>
    public const string IncompleteApiKey = "OPENAPIATTR0006";

    /// <summary>Two operations on the same method and path collide.</summary>
    public const string DuplicateOperation = "OPENAPIATTR0007";
}
