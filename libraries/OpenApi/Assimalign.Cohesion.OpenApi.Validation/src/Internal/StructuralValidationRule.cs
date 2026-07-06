using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi.Validation;

/// <summary>
/// Validates the structural shape of a document: presence of required fields, well-formed path templates,
/// and mutually exclusive field combinations.
/// </summary>
internal sealed class StructuralValidationRule : IOpenApiValidationRule
{
    public void Validate(OpenApiValidationContext context)
    {
        var document = context.Document;

        if (string.IsNullOrEmpty(document.Info.Title))
        {
            context.Error(OpenApiValidationRuleCodes.RequiredField, "The Info Object requires a non-empty 'title'.", JsonPointer.Of("info", "title"));
        }

        if (string.IsNullOrEmpty(document.Info.Version))
        {
            context.Error(OpenApiValidationRuleCodes.RequiredField, "The Info Object requires a non-empty 'version'.", JsonPointer.Of("info", "version"));
        }

        ValidateLicense(context, document.Info.License);

        if (document.SpecVersion == OpenApiSpecVersion.V3_0 && document.Paths is null)
        {
            context.Error(OpenApiValidationRuleCodes.RequiredField, "OpenAPI 3.0 requires the 'paths' field.", JsonPointer.Of("paths"));
        }

        if (document.ExternalDocs is not null && string.IsNullOrEmpty(document.ExternalDocs.Url))
        {
            context.Error(OpenApiValidationRuleCodes.RequiredField, "The External Documentation Object requires a non-empty 'url'.", JsonPointer.Of("externalDocs", "url"));
        }

        for (var index = 0; index < document.Servers.Count; index++)
        {
            if (string.IsNullOrEmpty(document.Servers[index].Url))
            {
                context.Error(OpenApiValidationRuleCodes.RequiredField, "The Server Object requires a non-empty 'url'.", JsonPointer.Of("servers", index.ToString(), "url"));
            }
        }

        ValidatePathTemplates(context, document);
        ValidateResponseDescriptions(context, document);
    }

    private static void ValidateLicense(OpenApiValidationContext context, OpenApiLicense? license)
    {
        if (license is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(license.Name))
        {
            context.Error(OpenApiValidationRuleCodes.RequiredField, "The License Object requires a non-empty 'name'.", JsonPointer.Of("info", "license", "name"));
        }

        if (license.Identifier is not null && license.Url is not null)
        {
            context.Error(OpenApiValidationRuleCodes.MutuallyExclusiveFields, "The License Object 'identifier' and 'url' fields are mutually exclusive.", JsonPointer.Of("info", "license"));
        }
    }

    private static void ValidatePathTemplates(OpenApiValidationContext context, OpenApiDocument document)
    {
        if (document.Paths is null)
        {
            return;
        }

        foreach (var path in document.Paths.Items.Keys)
        {
            if (!path.StartsWith('/'))
            {
                context.Error(OpenApiValidationRuleCodes.InvalidPathTemplate, $"Path template '{path}' must begin with a forward slash.", JsonPointer.Of("paths", path));
            }
        }
    }

    private static void ValidateResponseDescriptions(OpenApiValidationContext context, OpenApiDocument document)
    {
        foreach (var entry in OpenApiOperationWalker.Enumerate(document))
        {
            var responses = entry.Operation.Responses;
            if (responses is null)
            {
                continue;
            }

            foreach (var response in responses.Items)
            {
                // OpenAPI 3.2 made the response description optional.
                if (document.SpecVersion != OpenApiSpecVersion.V3_2
                    && response.Value.Reference is null && string.IsNullOrEmpty(response.Value.Description))
                {
                    context.Error(
                        OpenApiValidationRuleCodes.RequiredField,
                        "The Response Object requires a non-empty 'description' before OpenAPI 3.2.",
                        JsonPointer.Append(entry.Pointer, "responses", response.Key, "description"));
                }
            }
        }
    }
}
