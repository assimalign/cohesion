namespace Assimalign.Cohesion.OpenApi.Validation;

/// <summary>
/// Validates that fields and features used by the document are valid for its declared
/// <see cref="OpenApiSpecVersion"/>, consulting <see cref="OpenApiVersionCapabilities"/> so the rule never
/// drifts from the capability matrix that serialization uses.
/// </summary>
internal sealed class VersionPlacementRule : IOpenApiValidationRule
{
    public void Validate(OpenApiValidationContext context)
    {
        var document = context.Document;

        Check(context, document.Info.Summary is not null, OpenApiFeature.InfoSummary, JsonPointer.Of("info", "summary"), "The Info Object 'summary' field");
        Check(context, document.Info.License?.Identifier is not null, OpenApiFeature.LicenseIdentifier, JsonPointer.Of("info", "license", "identifier"), "The License Object 'identifier' field");
        Check(context, document.Webhooks.Count > 0, OpenApiFeature.Webhooks, JsonPointer.Of("webhooks"), "The top-level 'webhooks' field");
        Check(context, document.JsonSchemaDialect is not null, OpenApiFeature.JsonSchemaDialect, JsonPointer.Of("jsonSchemaDialect"), "The top-level 'jsonSchemaDialect' field");
        Check(context, document.Self is not null, OpenApiFeature.DocumentSelf, JsonPointer.Of("$self"), "The top-level '$self' field");
        Check(context, document.Components?.PathItems.Count > 0, OpenApiFeature.ComponentsPathItems, JsonPointer.Of("components", "pathItems"), "The Components Object 'pathItems' field");

        ValidateTags(context, document);
        ValidateSecuritySchemes(context, document);
        ValidateComponentSchemas(context, document);
    }

    private static void ValidateTags(OpenApiValidationContext context, OpenApiDocument document)
    {
        for (var index = 0; index < document.Tags.Count; index++)
        {
            var tag = document.Tags[index];
            var hasExtended = tag.Summary is not null || tag.Parent is not null || tag.Kind is not null;
            Check(context, hasExtended, OpenApiFeature.TagExtendedMetadata, JsonPointer.Of("tags", index.ToString()), "The Tag Object 'summary', 'parent', and 'kind' fields");
        }
    }

    private static void ValidateSecuritySchemes(OpenApiValidationContext context, OpenApiDocument document)
    {
        if (document.Components is null)
        {
            return;
        }

        foreach (var scheme in document.Components.SecuritySchemes)
        {
            var pointer = JsonPointer.Of("components", "securitySchemes", scheme.Key);
            Check(context, scheme.Value.Type == SecuritySchemeType.MutualTLS, OpenApiFeature.MutualTlsSecurityScheme, pointer, "The 'mutualTLS' security scheme type");
            Check(context, scheme.Value.Flows?.DeviceAuthorization is not null, OpenApiFeature.OAuthDeviceAuthorizationFlow, JsonPointer.Append(pointer, "flows", "deviceAuthorization"), "The OAuth 'deviceAuthorization' flow");
        }
    }

    private static void ValidateComponentSchemas(OpenApiValidationContext context, OpenApiDocument document)
    {
        if (document.Components is null)
        {
            return;
        }

        foreach (var schema in document.Components.Schemas)
        {
            var pointer = JsonPointer.Of("components", "schemas", schema.Key);
            Check(context, schema.Value.Const is not null, OpenApiFeature.SchemaConst, JsonPointer.Append(pointer, "const"), "The schema 'const' keyword");
            Check(context, schema.Value.Examples.Count > 0, OpenApiFeature.SchemaExamples, JsonPointer.Append(pointer, "examples"), "The schema 'examples' keyword");
        }
    }

    private static void Check(OpenApiValidationContext context, bool isPresent, OpenApiFeature feature, string pointer, string fieldDescription)
    {
        if (isPresent && !OpenApiVersionCapabilities.Supports(feature, context.Document.SpecVersion))
        {
            context.Error(
                OpenApiValidationRuleCodes.UnsupportedInVersion,
                $"{fieldDescription} is not supported in OpenAPI {OpenApiVersionCapabilities.GetVersionString(context.Document.SpecVersion)}.",
                pointer);
        }
    }
}
