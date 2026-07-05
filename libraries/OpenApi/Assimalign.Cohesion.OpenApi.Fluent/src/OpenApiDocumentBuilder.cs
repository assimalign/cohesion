using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// The entry point for fluently authoring an <see cref="OpenApiDocument"/>. Create a builder with
/// <see cref="Create(OpenApiSpecVersion, string, string)"/>, chain the authoring members, and call
/// <see cref="IOpenApiDocumentBuilder.Build"/>.
/// </summary>
/// <example>
/// <code>
/// var document = OpenApiDocumentBuilder.Create(OpenApiSpecVersion.V3_1, "Petstore", "1.0.0")
///     .Server("https://api.example.com")
///     .Path("/pets/{id}", path => path
///         .Operation(OperationType.Get, op => op
///             .OperationId("getPet")
///             .Response("200", r => r.Description("A pet")
///                 .Content("application/json", m => m.SchemaReference("#/components/schemas/Pet")))))
///     .Components(c => c
///         .Schema("Pet", s => s.Type(SchemaType.Object).Property("name", p => p.Type(SchemaType.String)).Required("name")))
///     .Build();
/// </code>
/// </example>
public sealed class OpenApiDocumentBuilder : IOpenApiDocumentBuilder
{
    private readonly OpenApiDocument _document;
    private readonly OpenApiSpecVersion _version;

    private OpenApiDocumentBuilder(OpenApiSpecVersion version, string title, string apiVersion)
    {
        _version = version;
        _document = new OpenApiDocument
        {
            SpecVersion = version,
            Info = new OpenApiInfo { Title = title, Version = apiVersion }
        };
    }

    /// <summary>
    /// Creates a document builder targeting an OpenAPI line, seeded with the required API title and version.
    /// </summary>
    /// <param name="version">The OpenAPI line the document targets.</param>
    /// <param name="title">The API title (a required Info field).</param>
    /// <param name="apiVersion">The API version (a required Info field), distinct from the OpenAPI line.</param>
    /// <returns>A new document builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="title"/> or <paramref name="apiVersion"/> is <see langword="null"/>.</exception>
    public static IOpenApiDocumentBuilder Create(OpenApiSpecVersion version, string title, string apiVersion)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(apiVersion);
        return new OpenApiDocumentBuilder(version, title, apiVersion);
    }

    /// <inheritdoc/>
    public IOpenApiDocumentBuilder Info(Action<IOpenApiInfoBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(new OpenApiInfoBuilder(_document.Info, _version));
        return this;
    }

    /// <inheritdoc/>
    public IOpenApiDocumentBuilder Self(string self)
    {
        ArgumentNullException.ThrowIfNull(self);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.DocumentSelf, "The top-level '$self' field");
        _document.Self = self;
        return this;
    }

    /// <inheritdoc/>
    public IOpenApiDocumentBuilder JsonSchemaDialect(string dialect)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.JsonSchemaDialect, "The top-level 'jsonSchemaDialect' field");
        _document.JsonSchemaDialect = dialect;
        return this;
    }

    /// <inheritdoc/>
    public IOpenApiDocumentBuilder Server(string url, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        _document.Servers.Add(new OpenApiServer { Url = url, Description = description });
        return this;
    }

    /// <inheritdoc/>
    public IOpenApiDocumentBuilder Path(string template, Action<IOpenApiPathItemBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(configure);
        _document.Paths ??= new OpenApiPaths();
        var builder = new OpenApiPathItemBuilder(_version);
        configure(builder);
        _document.Paths.Items[template] = builder.Build();
        return this;
    }

    /// <inheritdoc/>
    public IOpenApiDocumentBuilder Webhook(string name, Action<IOpenApiPathItemBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.Webhooks, "The top-level 'webhooks' field");
        var builder = new OpenApiPathItemBuilder(_version);
        configure(builder);
        _document.Webhooks[name] = builder.Build();
        return this;
    }

    /// <inheritdoc/>
    public IOpenApiDocumentBuilder Components(Action<IOpenApiComponentsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _document.Components ??= new OpenApiComponents();
        configure(new OpenApiComponentsBuilder(_document.Components, _version));
        return this;
    }

    /// <inheritdoc/>
    public IOpenApiDocumentBuilder Security(string scheme, params string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        var requirement = new OpenApiSecurityRequirement();
        requirement.Schemes[scheme] = new List<string>(scopes ?? []);
        _document.Security.Add(requirement);
        return this;
    }

    /// <inheritdoc/>
    public IOpenApiDocumentBuilder Tag(string name, Action<IOpenApiTagBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var tag = new OpenApiTag { Name = name };
        configure(new OpenApiTagBuilder(tag, _version));
        _document.Tags.Add(tag);
        return this;
    }

    /// <inheritdoc/>
    public IOpenApiDocumentBuilder Extension(string name, OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        _document.Extensions[name] = value;
        return this;
    }

    /// <inheritdoc/>
    public OpenApiDocument Build() => _document;
}
