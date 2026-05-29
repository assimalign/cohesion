namespace Assimalign.Cohesion.OpenApi.Validation;

/// <summary>
/// The stable diagnostic codes produced by the built-in validation rules. Codes in the <c>1xxx</c> range
/// are structural, <c>2xxx</c> are version-placement, and <c>3xxx</c> are semantic.
/// </summary>
public static class OpenApiValidationRuleCodes
{
    /// <summary>A required field is missing or empty.</summary>
    public const string RequiredField = "OPENAPI1001";

    /// <summary>A path template does not begin with a forward slash.</summary>
    public const string InvalidPathTemplate = "OPENAPI1002";

    /// <summary>Two mutually exclusive fields are both present.</summary>
    public const string MutuallyExclusiveFields = "OPENAPI1003";

    /// <summary>A field or feature is used that is not valid for the document's declared version.</summary>
    public const string UnsupportedInVersion = "OPENAPI2001";

    /// <summary>Two or more operations declare the same <c>operationId</c>.</summary>
    public const string DuplicateOperationId = "OPENAPI3001";

    /// <summary>A path template placeholder has no matching path parameter.</summary>
    public const string MissingPathParameter = "OPENAPI3002";

    /// <summary>A declared path parameter does not appear in the path template.</summary>
    public const string UndeclaredPathParameter = "OPENAPI3003";

    /// <summary>A path parameter is not marked as required.</summary>
    public const string PathParameterNotRequired = "OPENAPI3004";

    /// <summary>A security requirement references a security scheme that is not defined in components.</summary>
    public const string UnknownSecurityScheme = "OPENAPI3005";

    /// <summary>A responses map key is not a valid status code, status-code range, or <c>default</c>.</summary>
    public const string InvalidResponseKey = "OPENAPI3006";

    /// <summary>A parameter declares both <c>schema</c> and <c>content</c>, or neither.</summary>
    public const string ParameterSchemaAndContent = "OPENAPI3007";
}
