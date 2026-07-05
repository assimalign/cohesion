using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// The entry point for fluently authoring an <see cref="OpenApiDocument"/>. Create a builder with
/// <see cref="Create(OpenApiSpecVersion, string, string)"/>, chain the authoring members, and call
/// <see cref="Build"/>.
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
// Deviates from AGENTS.md interface-first rule per design decision: OpenApi is a published standard, so the builders are concrete conveniences over the public model rather than an imposed interface contract.
public sealed class OpenApiDocumentBuilder
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
    public static OpenApiDocumentBuilder Create(OpenApiSpecVersion version, string title, string apiVersion)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(apiVersion);
        return new OpenApiDocumentBuilder(version, title, apiVersion);
    }

    /// <summary>Configures the API metadata beyond the required title and version.</summary>
    /// <param name="configure">Configures the info object.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiDocumentBuilder Info(Action<OpenApiInfoBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(new OpenApiInfoBuilder(_document.Info, _version));
        return this;
    }

    /// <summary>Sets the <c>$self</c> document identity URI (OpenAPI 3.2+).</summary>
    /// <param name="self">The self URI.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiDocumentBuilder Self(string self)
    {
        ArgumentNullException.ThrowIfNull(self);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.DocumentSelf, "The top-level '$self' field");
        _document.Self = self;
        return this;
    }

    /// <summary>Sets the default JSON Schema dialect for the document's schemas (OpenAPI 3.1+).</summary>
    /// <param name="dialect">The dialect URI.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiDocumentBuilder JsonSchemaDialect(string dialect)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.JsonSchemaDialect, "The top-level 'jsonSchemaDialect' field");
        _document.JsonSchemaDialect = dialect;
        return this;
    }

    /// <summary>Adds a server.</summary>
    /// <param name="url">The server URL.</param>
    /// <param name="description">An optional server description.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiDocumentBuilder Server(string url, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        _document.Servers.Add(new OpenApiServer { Url = url, Description = description });
        return this;
    }

    /// <summary>Adds a path and its operations.</summary>
    /// <param name="template">The path template, for example <c>/pets/{id}</c>.</param>
    /// <param name="configure">Configures the path item.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiDocumentBuilder Path(string template, Action<OpenApiPathItemBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(configure);
        _document.Paths ??= new OpenApiPaths();
        var builder = new OpenApiPathItemBuilder(_version);
        configure(builder);
        _document.Paths.Items[template] = builder.Build();
        return this;
    }

    /// <summary>Adds a webhook (OpenAPI 3.1+).</summary>
    /// <param name="name">The webhook name.</param>
    /// <param name="configure">Configures the webhook path item.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiDocumentBuilder Webhook(string name, Action<OpenApiPathItemBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.Webhooks, "The top-level 'webhooks' field");
        var builder = new OpenApiPathItemBuilder(_version);
        configure(builder);
        _document.Webhooks[name] = builder.Build();
        return this;
    }

    /// <summary>Configures the reusable components.</summary>
    /// <param name="configure">Configures the components.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiDocumentBuilder Components(Action<OpenApiComponentsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _document.Components ??= new OpenApiComponents();
        configure(new OpenApiComponentsBuilder(_document.Components, _version));
        return this;
    }

    /// <summary>Adds a document-level security requirement.</summary>
    /// <param name="scheme">The security scheme name.</param>
    /// <param name="scopes">The required scopes, if any.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiDocumentBuilder Security(string scheme, params string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        var requirement = new OpenApiSecurityRequirement();
        requirement.Schemes[scheme] = new List<string>(scopes ?? []);
        _document.Security.Add(requirement);
        return this;
    }

    /// <summary>Adds a tag.</summary>
    /// <param name="name">The tag name.</param>
    /// <param name="configure">Configures the tag.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiDocumentBuilder Tag(string name, Action<OpenApiTagBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var tag = new OpenApiTag { Name = name };
        configure(new OpenApiTagBuilder(tag, _version));
        _document.Tags.Add(tag);
        return this;
    }

    /// <summary>Sets additional external documentation for the API.</summary>
    /// <param name="url">The documentation URL.</param>
    /// <param name="description">An optional description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiDocumentBuilder ExternalDocs(string url, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        _document.ExternalDocs = new OpenApiExternalDocumentation { Url = url, Description = description };
        return this;
    }

    /// <summary>Sets a specification extension (an <c>x-</c> field) on the document root.</summary>
    /// <param name="name">The extension name, including the <c>x-</c> prefix.</param>
    /// <param name="value">The extension value.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiDocumentBuilder Extension(string name, OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        _document.Extensions[name] = value;
        return this;
    }

    /// <summary>Builds the document.</summary>
    /// <returns>The assembled <see cref="OpenApiDocument"/> with its <see cref="OpenApiDocument.SpecVersion"/> set to the target line.</returns>
    public OpenApiDocument Build() => _document;
}
