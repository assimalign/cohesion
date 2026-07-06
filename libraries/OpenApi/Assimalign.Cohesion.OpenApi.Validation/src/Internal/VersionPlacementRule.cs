using System.Collections.Generic;

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
        Check(context, document.Components?.MediaTypes.Count > 0, OpenApiFeature.ComponentsMediaTypes, JsonPointer.Of("components", "mediaTypes"), "The Components Object 'mediaTypes' field");

        ValidateServers(context, document.Servers, JsonPointer.Of("servers"));
        ValidateTags(context, document);
        ValidatePathItems(context, document);
        ValidateOperations(context, document);
        ValidateSecuritySchemes(context, document);
        ValidateComponentSchemas(context, document);
    }

    private static void ValidateServers(OpenApiValidationContext context, IList<OpenApiServer> servers, string pointer)
    {
        for (var index = 0; index < servers.Count; index++)
        {
            Check(context, servers[index].Name is not null, OpenApiFeature.ServerName, JsonPointer.Append(pointer, index.ToString(), "name"), "The Server Object 'name' field");
        }
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

    private static void ValidatePathItems(OpenApiValidationContext context, OpenApiDocument document)
    {
        if (document.Paths is not null)
        {
            ValidatePathItemMap(context, document.Paths.Items, "paths");
        }

        ValidatePathItemMap(context, document.Webhooks, "webhooks");
    }

    private static void ValidatePathItemMap(OpenApiValidationContext context, IDictionary<string, OpenApiPathItem> items, string root)
    {
        foreach (var pair in items)
        {
            Check(context, pair.Value.Operations.ContainsKey(OperationType.Query), OpenApiFeature.PathItemQueryOperation, JsonPointer.Of(root, pair.Key, "query"), "The Path Item Object 'query' operation");
            Check(context, pair.Value.AdditionalOperations.Count > 0, OpenApiFeature.PathItemAdditionalOperations, JsonPointer.Of(root, pair.Key, "additionalOperations"), "The Path Item Object 'additionalOperations' field");
        }
    }

    private static void ValidateOperations(OpenApiValidationContext context, OpenApiDocument document)
    {
        foreach (var entry in OpenApiOperationWalker.Enumerate(document))
        {
            var operation = entry.Operation;

            for (var index = 0; index < operation.Parameters.Count; index++)
            {
                var parameter = operation.Parameters[index];
                var pointer = JsonPointer.Append(entry.Pointer, "parameters", index.ToString());
                Check(context, parameter.In == ParameterLocation.Querystring, OpenApiFeature.ParameterQuerystringLocation, JsonPointer.Append(pointer, "in"), "The 'querystring' parameter location");
                Check(context, parameter.Style == ParameterStyle.Cookie, OpenApiFeature.ParameterCookieStyle, JsonPointer.Append(pointer, "style"), "The 'cookie' parameter style");
                ValidateExamples(context, parameter.Examples, JsonPointer.Append(pointer, "examples"));
                ValidateContent(context, parameter.Content, JsonPointer.Append(pointer, "content"));
            }

            if (operation.RequestBody is not null)
            {
                ValidateContent(context, operation.RequestBody.Content, JsonPointer.Append(entry.Pointer, "requestBody", "content"));
            }

            if (operation.Responses is not null)
            {
                foreach (var response in operation.Responses.Items)
                {
                    var pointer = JsonPointer.Append(entry.Pointer, "responses", response.Key);
                    Check(context, response.Value.Summary is not null, OpenApiFeature.ResponseSummary, JsonPointer.Append(pointer, "summary"), "The Response Object 'summary' field");
                    ValidateContent(context, response.Value.Content, JsonPointer.Append(pointer, "content"));
                }
            }
        }
    }

    private static void ValidateContent(OpenApiValidationContext context, IDictionary<string, OpenApiMediaType> content, string pointer)
    {
        foreach (var pair in content)
        {
            var media = pair.Value;
            var mediaPointer = JsonPointer.Append(pointer, pair.Key);
            Check(context, media.Reference is not null, OpenApiFeature.MediaTypeReference, mediaPointer, "A Reference Object used as a 'content' map value");
            Check(context, media.ItemSchema is not null, OpenApiFeature.MediaTypeStreamingFields, JsonPointer.Append(mediaPointer, "itemSchema"), "The Media Type Object 'itemSchema' field");
            Check(context, media.PrefixEncoding.Count > 0, OpenApiFeature.MediaTypeStreamingFields, JsonPointer.Append(mediaPointer, "prefixEncoding"), "The Media Type Object 'prefixEncoding' field");
            Check(context, media.ItemEncoding is not null, OpenApiFeature.MediaTypeStreamingFields, JsonPointer.Append(mediaPointer, "itemEncoding"), "The Media Type Object 'itemEncoding' field");
            ValidateExamples(context, media.Examples, JsonPointer.Append(mediaPointer, "examples"));

            foreach (var encoding in media.Encoding)
            {
                var encodingPointer = JsonPointer.Append(mediaPointer, "encoding", encoding.Key);
                var hasNested = encoding.Value.Encoding.Count > 0 || encoding.Value.PrefixEncoding.Count > 0 || encoding.Value.ItemEncoding is not null;
                Check(context, hasNested, OpenApiFeature.MediaTypeStreamingFields, encodingPointer, "The Encoding Object nested 'encoding', 'prefixEncoding', and 'itemEncoding' fields");
            }
        }
    }

    private static void ValidateExamples(OpenApiValidationContext context, IDictionary<string, OpenApiExample> examples, string pointer)
    {
        foreach (var pair in examples)
        {
            var hasDataFields = pair.Value.DataValue is not null || pair.Value.SerializedValue is not null;
            Check(context, hasDataFields, OpenApiFeature.ExampleDataAndSerializedValues, JsonPointer.Append(pointer, pair.Key), "The Example Object 'dataValue' and 'serializedValue' fields");
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
            Check(context, scheme.Value.OAuth2MetadataUrl is not null, OpenApiFeature.SecuritySchemeOAuth2MetadataUrl, JsonPointer.Append(pointer, "oauth2MetadataUrl"), "The Security Scheme Object 'oauth2MetadataUrl' field");
            Check(context, scheme.Value.Deprecated, OpenApiFeature.SecuritySchemeDeprecated, JsonPointer.Append(pointer, "deprecated"), "The Security Scheme Object 'deprecated' field");
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
            ValidateSchemaFeatures(context, schema.Value, pointer);
        }
    }

    private static void ValidateSchemaFeatures(OpenApiValidationContext context, OpenApiSchema schema, string pointer)
    {
        Check(context, schema.Const is not null, OpenApiFeature.SchemaConst, JsonPointer.Append(pointer, "const"), "The schema 'const' keyword");
        Check(context, schema.Examples.Count > 0, OpenApiFeature.SchemaExamples, JsonPointer.Append(pointer, "examples"), "The schema 'examples' keyword");
        Check(context, schema.Types.Count > 1, OpenApiFeature.SchemaTypeArray, JsonPointer.Append(pointer, "type"), "A schema 'type' array with multiple entries");
        Check(context, schema.BooleanValue.HasValue, OpenApiFeature.SchemaBooleanForm, pointer, "The boolean schema form");
        Check(context, schema.Reference is not null && HasKeywordsBesideReference(schema), OpenApiFeature.SchemaReferenceSiblingKeywords, pointer, "Keywords alongside '$ref' in a Schema Object");
        Check(context, HasExtendedVocabulary(schema), OpenApiFeature.SchemaExtendedVocabulary, pointer, "The JSON Schema draft 2020-12 keywords used by this schema");
        Check(context, schema.Discriminator?.DefaultMapping is not null, OpenApiFeature.DiscriminatorDefaultMapping, JsonPointer.Append(pointer, "discriminator", "defaultMapping"), "The Discriminator Object 'defaultMapping' field");
        Check(context, schema.Xml?.NodeType is not null, OpenApiFeature.XmlNodeType, JsonPointer.Append(pointer, "xml", "nodeType"), "The XML Object 'nodeType' field");
    }

    private static bool HasKeywordsBesideReference(OpenApiSchema schema) =>
        schema.Types.Count > 0
        || schema.Title is not null
        || schema.Description is not null
        || schema.Default is not null
        || schema.Const is not null
        || schema.Enum.Count > 0
        || schema.Properties.Count > 0
        || schema.Required.Count > 0
        || schema.Items is not null
        || schema.AllOf.Count > 0
        || schema.AnyOf.Count > 0
        || schema.OneOf.Count > 0
        || schema.Not is not null
        || HasExtendedVocabulary(schema);

    private static bool HasExtendedVocabulary(OpenApiSchema schema) =>
        schema.Id is not null
        || schema.Dialect is not null
        || schema.Anchor is not null
        || schema.DynamicRef is not null
        || schema.DynamicAnchor is not null
        || schema.Comment is not null
        || schema.Defs.Count > 0
        || schema.PatternProperties.Count > 0
        || schema.PropertyNames is not null
        || schema.UnevaluatedProperties is not null
        || schema.DependentRequired.Count > 0
        || schema.DependentSchemas.Count > 0
        || schema.PrefixItems.Count > 0
        || schema.Contains is not null
        || schema.MinContains.HasValue
        || schema.MaxContains.HasValue
        || schema.UnevaluatedItems is not null
        || schema.If is not null
        || schema.Then is not null
        || schema.Else is not null
        || schema.ContentEncoding is not null
        || schema.ContentMediaType is not null
        || schema.ContentSchema is not null;

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
