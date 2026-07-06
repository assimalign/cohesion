using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// Maps OpenApi authoring attributes to the flat intermediate metadata, applying the mapping rules and
/// reporting invalid combinations as <see cref="OpenApiMetadataDiagnostic"/> values.
/// </summary>
/// <remarks>
/// This mapper embodies the attribute-to-metadata contract at run time. The AOT-safe source generator
/// mirrors these same rules over Roslyn symbols, so an application's compile-time output matches what
/// this mapper would produce from the equivalent attribute instances. The mapper itself performs no
/// member reflection — it reads the attribute values it is handed, and derives schema component
/// references from a model type's name only.
/// </remarks>
public static class OpenApiAttributeMapper
{
    private const string SchemaComponentPrefix = "#/components/schemas/";

    /// <summary>
    /// Resolves the schema component reference for a model type by its name.
    /// </summary>
    /// <param name="modelType">The model type.</param>
    /// <returns>A reference of the form <c>#/components/schemas/{TypeName}</c>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="modelType"/> is <see langword="null"/>.</exception>
    public static string ResolveSchemaReference(Type modelType)
    {
        ArgumentNullException.ThrowIfNull(modelType);
        return SchemaComponentPrefix + modelType.Name;
    }

    /// <summary>
    /// Maps an operation and its associated attributes to operation metadata.
    /// </summary>
    /// <param name="operation">The operation attribute.</param>
    /// <param name="parameters">The parameter attributes, if any.</param>
    /// <param name="requestBody">The request body attribute, if any.</param>
    /// <param name="responses">The response attributes, if any.</param>
    /// <param name="security">The operation-level security requirements, if any.</param>
    /// <param name="diagnostics">Receives findings for invalid combinations.</param>
    /// <returns>The mapped operation metadata.</returns>
    public static OpenApiOperationMetadata MapOperation(
        OpenApiOperationAttribute operation,
        IEnumerable<OpenApiParameterAttribute>? parameters,
        OpenApiRequestBodyAttribute? requestBody,
        IEnumerable<OpenApiResponseAttribute>? responses,
        IEnumerable<OpenApiSecurityRequirementAttribute>? security,
        ICollection<OpenApiMetadataDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var target = $"{operation.Method} {operation.Path}";

        if (string.IsNullOrEmpty(operation.Path))
        {
            diagnostics.Add(new OpenApiMetadataDiagnostic(
                OpenApiMetadataSeverity.Error, OpenApiMetadataDiagnosticCodes.MissingPath,
                "An operation attribute must declare a non-empty path.", target));
        }

        var mappedParameters = new List<OpenApiParameterMetadata>();
        foreach (var parameter in parameters ?? [])
        {
            mappedParameters.Add(MapParameter(parameter, diagnostics));
        }

        var mappedResponses = new List<OpenApiResponseMetadata>();
        foreach (var response in responses ?? [])
        {
            mappedResponses.Add(MapResponse(response, diagnostics));
        }

        var mappedSecurity = new List<OpenApiSecurityRequirementMetadata>();
        foreach (var requirement in security ?? [])
        {
            mappedSecurity.Add(MapSecurityRequirement(requirement));
        }

        return new OpenApiOperationMetadata
        {
            Method = operation.Method,
            Path = operation.Path,
            OperationId = operation.OperationId,
            Summary = operation.Summary,
            Description = operation.Description,
            Tags = operation.Tags ?? [],
            Deprecated = operation.Deprecated,
            Parameters = mappedParameters,
            RequestBody = requestBody is null ? null : MapRequestBody(requestBody, diagnostics, target),
            Responses = mappedResponses,
            Security = mappedSecurity
        };
    }

    /// <summary>Maps a parameter attribute to parameter metadata.</summary>
    /// <param name="attribute">The parameter attribute.</param>
    /// <param name="diagnostics">Receives findings.</param>
    /// <returns>The mapped parameter metadata.</returns>
    public static OpenApiParameterMetadata MapParameter(OpenApiParameterAttribute attribute, ICollection<OpenApiMetadataDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var required = attribute.Required;
        if (attribute.In == ParameterLocation.Path && !required)
        {
            required = true;
            diagnostics.Add(new OpenApiMetadataDiagnostic(
                OpenApiMetadataSeverity.Warning, OpenApiMetadataDiagnosticCodes.PathParameterRequired,
                $"Path parameter '{attribute.Name}' was not marked required and has been corrected to required.", attribute.Name));
        }

        return new OpenApiParameterMetadata
        {
            Name = attribute.Name,
            In = attribute.In,
            Description = attribute.Description,
            Required = required,
            Deprecated = attribute.Deprecated,
            SchemaType = ToSchemaType(attribute.SchemaType),
            Format = attribute.Format
        };
    }

    /// <summary>Maps a request body attribute to request body metadata.</summary>
    /// <param name="attribute">The request body attribute.</param>
    /// <param name="diagnostics">Receives findings.</param>
    /// <returns>The mapped request body metadata.</returns>
    public static OpenApiRequestBodyMetadata MapRequestBody(OpenApiRequestBodyAttribute attribute, ICollection<OpenApiMetadataDiagnostic> diagnostics) =>
        MapRequestBody(attribute, diagnostics, attribute?.ContentType ?? string.Empty);

    private static OpenApiRequestBodyMetadata MapRequestBody(OpenApiRequestBodyAttribute attribute, ICollection<OpenApiMetadataDiagnostic> diagnostics, string target)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(diagnostics);

        return new OpenApiRequestBodyMetadata
        {
            ContentType = attribute.ContentType,
            Description = attribute.Description,
            Required = attribute.Required,
            SchemaReference = ResolveSchema(attribute.ModelType, attribute.SchemaReference, target, diagnostics)
        };
    }

    /// <summary>Maps a response attribute to response metadata.</summary>
    /// <param name="attribute">The response attribute.</param>
    /// <param name="diagnostics">Receives findings.</param>
    /// <returns>The mapped response metadata.</returns>
    public static OpenApiResponseMetadata MapResponse(OpenApiResponseAttribute attribute, ICollection<OpenApiMetadataDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(diagnostics);

        return new OpenApiResponseMetadata
        {
            StatusCode = attribute.StatusCode,
            Description = attribute.Description,
            ContentType = attribute.ContentType,
            SchemaReference = ResolveSchema(attribute.ModelType, attribute.SchemaReference, attribute.StatusCode, diagnostics)
        };
    }

    /// <summary>Maps a schema attribute and its property attributes to schema metadata.</summary>
    /// <param name="attribute">The schema attribute.</param>
    /// <param name="typeName">The annotated type's name, used when the attribute does not name the component.</param>
    /// <param name="properties">The member name and property attribute pairs.</param>
    /// <param name="diagnostics">Receives findings.</param>
    /// <returns>The mapped schema metadata.</returns>
    public static OpenApiSchemaMetadata MapSchema(
        OpenApiSchemaAttribute attribute,
        string typeName,
        IEnumerable<(string MemberName, OpenApiSchemaPropertyAttribute Attribute)>? properties,
        ICollection<OpenApiMetadataDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(typeName);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var mappedProperties = new List<OpenApiSchemaPropertyMetadata>();
        foreach (var (memberName, propertyAttribute) in properties ?? [])
        {
            mappedProperties.Add(new OpenApiSchemaPropertyMetadata
            {
                Name = propertyAttribute.Name ?? memberName,
                Description = propertyAttribute.Description,
                Required = propertyAttribute.Required,
                Nullable = propertyAttribute.Nullable,
                SchemaType = ToSchemaType(propertyAttribute.SchemaType),
                Format = propertyAttribute.Format,
                SchemaReference = propertyAttribute.SchemaReference
            });
        }

        return new OpenApiSchemaMetadata
        {
            Name = attribute.Name ?? typeName,
            Title = attribute.Title,
            Description = attribute.Description,
            Type = attribute.Type,
            Deprecated = attribute.Deprecated,
            Properties = mappedProperties
        };
    }

    /// <summary>Maps an example attribute to example metadata.</summary>
    /// <param name="attribute">The example attribute.</param>
    /// <param name="diagnostics">Receives findings.</param>
    /// <returns>The mapped example metadata.</returns>
    public static OpenApiExampleMetadata MapExample(OpenApiExampleAttribute attribute, ICollection<OpenApiMetadataDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (attribute.Value is not null && attribute.ExternalValue is not null)
        {
            diagnostics.Add(new OpenApiMetadataDiagnostic(
                OpenApiMetadataSeverity.Error, OpenApiMetadataDiagnosticCodes.AmbiguousExample,
                $"Example '{attribute.Name}' declares both an embedded value and an external value.", attribute.Name));
        }
        else if (attribute.Value is null && attribute.ExternalValue is null)
        {
            diagnostics.Add(new OpenApiMetadataDiagnostic(
                OpenApiMetadataSeverity.Warning, OpenApiMetadataDiagnosticCodes.EmptyExample,
                $"Example '{attribute.Name}' declares neither an embedded value nor an external value.", attribute.Name));
        }

        return new OpenApiExampleMetadata
        {
            Name = attribute.Name,
            Summary = attribute.Summary,
            Description = attribute.Description,
            Value = attribute.Value,
            ExternalValue = attribute.ExternalValue
        };
    }

    /// <summary>Maps a tag attribute to tag metadata.</summary>
    /// <param name="attribute">The tag attribute.</param>
    /// <returns>The mapped tag metadata.</returns>
    public static OpenApiTagMetadata MapTag(OpenApiTagAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return new OpenApiTagMetadata
        {
            Name = attribute.Name,
            Description = attribute.Description,
            Summary = attribute.Summary,
            Parent = attribute.Parent,
            Kind = attribute.Kind
        };
    }

    /// <summary>Maps a security scheme attribute to security scheme metadata.</summary>
    /// <param name="attribute">The security scheme attribute.</param>
    /// <param name="diagnostics">Receives findings.</param>
    /// <returns>The mapped security scheme metadata.</returns>
    public static OpenApiSecuritySchemeMetadata MapSecurityScheme(OpenApiSecuritySchemeAttribute attribute, ICollection<OpenApiMetadataDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (attribute.Type == SecuritySchemeType.ApiKey && (string.IsNullOrEmpty(attribute.ParameterName) || attribute.In is null))
        {
            diagnostics.Add(new OpenApiMetadataDiagnostic(
                OpenApiMetadataSeverity.Error, OpenApiMetadataDiagnosticCodes.IncompleteApiKey,
                $"API key security scheme '{attribute.Name}' requires both a parameter name and a location.", attribute.Name));
        }

        return new OpenApiSecuritySchemeMetadata
        {
            Name = attribute.Name,
            Type = attribute.Type,
            Description = attribute.Description,
            ParameterName = attribute.ParameterName,
            In = attribute.In,
            Scheme = attribute.Scheme,
            BearerFormat = attribute.BearerFormat,
            OpenIdConnectUrl = attribute.OpenIdConnectUrl
        };
    }

    /// <summary>Maps a security requirement attribute to security requirement metadata.</summary>
    /// <param name="attribute">The security requirement attribute.</param>
    /// <returns>The mapped security requirement metadata.</returns>
    public static OpenApiSecurityRequirementMetadata MapSecurityRequirement(OpenApiSecurityRequirementAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return new OpenApiSecurityRequirementMetadata { Scheme = attribute.Scheme, Scopes = attribute.Scopes };
    }

    /// <summary>
    /// Converts an attribute schema kind to a canonical schema type, mapping
    /// <see cref="OpenApiSchemaKind.Unspecified"/> to <see langword="null"/>.
    /// </summary>
    /// <param name="kind">The attribute schema kind.</param>
    /// <returns>The schema type, or <see langword="null"/> when unspecified.</returns>
    public static SchemaType? ToSchemaType(OpenApiSchemaKind kind) => kind switch
    {
        OpenApiSchemaKind.Boolean => SchemaType.Boolean,
        OpenApiSchemaKind.Object => SchemaType.Object,
        OpenApiSchemaKind.Array => SchemaType.Array,
        OpenApiSchemaKind.Number => SchemaType.Number,
        OpenApiSchemaKind.String => SchemaType.String,
        OpenApiSchemaKind.Integer => SchemaType.Integer,
        OpenApiSchemaKind.Null => SchemaType.Null,
        _ => null
    };

    private static string? ResolveSchema(Type? modelType, string? schemaReference, string target, ICollection<OpenApiMetadataDiagnostic> diagnostics)
    {
        if (modelType is not null && schemaReference is not null)
        {
            diagnostics.Add(new OpenApiMetadataDiagnostic(
                OpenApiMetadataSeverity.Error, OpenApiMetadataDiagnosticCodes.AmbiguousSchema,
                "A body declares both a model type and an explicit schema reference; use one.", target));
            return schemaReference;
        }

        if (schemaReference is not null)
        {
            return schemaReference;
        }

        return modelType is not null ? ResolveSchemaReference(modelType) : null;
    }
}
