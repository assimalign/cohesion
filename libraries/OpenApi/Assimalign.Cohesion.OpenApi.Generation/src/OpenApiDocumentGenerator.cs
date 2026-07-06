using System;
using System.Collections.Generic;

using Assimalign.Cohesion.OpenApi.Attributes;

namespace Assimalign.Cohesion.OpenApi.Generation;

/// <summary>
/// Assembles a canonical <see cref="OpenApiDocument"/> from the flat intermediate metadata. The output
/// is an ordinary model graph, so a generated document composes with fluent- or hand-built content and
/// serializes and validates like any other. Version-gated fields are populated only when the target
/// line supports them, so the generated document is version-clean.
/// </summary>
public static class OpenApiDocumentGenerator
{
    /// <summary>
    /// Generates an OpenAPI document from collected metadata.
    /// </summary>
    /// <param name="input">The collected metadata.</param>
    /// <param name="options">The target version and document metadata.</param>
    /// <returns>The assembled document.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="input"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public static OpenApiDocument Generate(OpenApiGenerationInput input, OpenApiGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);

        var version = options.Version;
        var document = new OpenApiDocument
        {
            SpecVersion = version,
            Info = new OpenApiInfo { Title = options.Title, Version = options.ApiVersion, Description = options.Description }
        };

        BuildComponents(document, input, version);
        BuildTags(document, input, version);
        BuildPaths(document, input, version);

        return document;
    }

    private static void BuildComponents(OpenApiDocument document, OpenApiGenerationInput input, OpenApiSpecVersion version)
    {
        if (input.Schemas.Count == 0 && input.SecuritySchemes.Count == 0)
        {
            return;
        }

        var components = new OpenApiComponents();

        foreach (var schema in input.Schemas)
        {
            components.Schemas[schema.Name] = BuildSchema(schema);
        }

        foreach (var scheme in input.SecuritySchemes)
        {
            components.SecuritySchemes[scheme.Name] = BuildSecurityScheme(scheme, version);
        }

        document.Components = components;
    }

    private static OpenApiSchema BuildSchema(OpenApiSchemaMetadata metadata)
    {
        var schema = new OpenApiSchema
        {
            Type = metadata.Type,
            Title = metadata.Title,
            Description = metadata.Description,
            Deprecated = metadata.Deprecated
        };

        foreach (var property in metadata.Properties)
        {
            schema.Properties[property.Name] = BuildPropertySchema(property);
            if (property.Required)
            {
                schema.Required.Add(property.Name);
            }
        }

        return schema;
    }

    private static OpenApiSchema BuildPropertySchema(OpenApiSchemaPropertyMetadata property)
    {
        if (property.SchemaReference is not null)
        {
            return new OpenApiSchema { Reference = new OpenApiReference { Ref = property.SchemaReference } };
        }

        return new OpenApiSchema
        {
            Type = property.SchemaType,
            Format = property.Format,
            Nullable = property.Nullable,
            Description = property.Description
        };
    }

    private static OpenApiSecurityScheme BuildSecurityScheme(OpenApiSecuritySchemeMetadata metadata, OpenApiSpecVersion version)
    {
        var scheme = new OpenApiSecurityScheme
        {
            Type = metadata.Type,
            Description = metadata.Description,
            Name = metadata.ParameterName,
            In = metadata.In,
            Scheme = metadata.Scheme,
            BearerFormat = metadata.BearerFormat,
            OpenIdConnectUrl = metadata.OpenIdConnectUrl
        };

        _ = version;
        return scheme;
    }

    private static void BuildTags(OpenApiDocument document, OpenApiGenerationInput input, OpenApiSpecVersion version)
    {
        var extended = OpenApiVersionCapabilities.Supports(OpenApiFeature.TagExtendedMetadata, version);

        foreach (var metadata in input.Tags)
        {
            var tag = new OpenApiTag
            {
                Name = metadata.Name,
                Description = metadata.Description
            };

            if (extended)
            {
                tag.Summary = metadata.Summary;
                tag.Parent = metadata.Parent;
                tag.Kind = metadata.Kind;
            }

            document.Tags.Add(tag);
        }
    }

    private static void BuildPaths(OpenApiDocument document, OpenApiGenerationInput input, OpenApiSpecVersion version)
    {
        if (input.Operations.Count == 0)
        {
            return;
        }

        var paths = new OpenApiPaths();

        foreach (var operation in input.Operations)
        {
            // A query operation is only valid from 3.2; skip it for earlier targets so the output stays clean.
            if (operation.Method == OperationType.Query && !OpenApiVersionCapabilities.Supports(OpenApiFeature.PathItemQueryOperation, version))
            {
                continue;
            }

            if (!paths.Items.TryGetValue(operation.Path, out var pathItem))
            {
                pathItem = new OpenApiPathItem();
                paths.Items[operation.Path] = pathItem;
            }

            pathItem.Operations[operation.Method] = BuildOperation(operation);
        }

        if (paths.Items.Count > 0)
        {
            document.Paths = paths;
        }
    }

    private static OpenApiOperation BuildOperation(OpenApiOperationMetadata metadata)
    {
        var operation = new OpenApiOperation
        {
            OperationId = metadata.OperationId,
            Summary = metadata.Summary,
            Description = metadata.Description,
            Deprecated = metadata.Deprecated
        };

        foreach (var tag in metadata.Tags)
        {
            operation.Tags.Add(tag);
        }

        foreach (var parameter in metadata.Parameters)
        {
            operation.Parameters.Add(BuildParameter(parameter));
        }

        if (metadata.RequestBody is not null)
        {
            operation.RequestBody = BuildRequestBody(metadata.RequestBody);
        }

        if (metadata.Responses.Count > 0)
        {
            operation.Responses = new OpenApiResponses();
            foreach (var response in metadata.Responses)
            {
                operation.Responses.Items[response.StatusCode] = BuildResponse(response);
            }
        }

        foreach (var requirement in metadata.Security)
        {
            var security = new OpenApiSecurityRequirement();
            security.Schemes[requirement.Scheme] = new List<string>(requirement.Scopes);
            operation.Security.Add(security);
        }

        return operation;
    }

    private static OpenApiParameter BuildParameter(OpenApiParameterMetadata metadata)
    {
        var parameter = new OpenApiParameter
        {
            Name = metadata.Name,
            In = metadata.In,
            Description = metadata.Description,
            Required = metadata.Required || metadata.In == ParameterLocation.Path,
            Deprecated = metadata.Deprecated
        };

        if (metadata.SchemaType is { } schemaType)
        {
            parameter.Schema = new OpenApiSchema { Type = schemaType, Format = metadata.Format };
        }

        return parameter;
    }

    private static OpenApiRequestBody BuildRequestBody(OpenApiRequestBodyMetadata metadata)
    {
        var body = new OpenApiRequestBody
        {
            Description = metadata.Description,
            Required = metadata.Required
        };

        body.Content[metadata.ContentType] = BuildMediaType(metadata.SchemaReference);
        return body;
    }

    private static OpenApiResponse BuildResponse(OpenApiResponseMetadata metadata)
    {
        var response = new OpenApiResponse { Description = metadata.Description ?? string.Empty };

        if (metadata.ContentType is not null)
        {
            response.Content[metadata.ContentType] = BuildMediaType(metadata.SchemaReference);
        }

        return response;
    }

    private static OpenApiMediaType BuildMediaType(string? schemaReference)
    {
        var media = new OpenApiMediaType();
        if (schemaReference is not null)
        {
            media.Schema = new OpenApiSchema { Reference = new OpenApiReference { Ref = schemaReference } };
        }

        return media;
    }
}
