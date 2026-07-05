using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for a complete <see cref="OpenApiDocument"/> targeting a specific OpenAPI line.
/// Version-gated members throw <see cref="OpenApiException"/> at authoring time when the target line
/// does not support them, surfacing mismatches where they are written.
/// </summary>
public interface IOpenApiDocumentBuilder
{
    /// <summary>Configures the API metadata beyond the required title and version.</summary>
    /// <param name="configure">Configures the info object.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiDocumentBuilder Info(Action<IOpenApiInfoBuilder> configure);

    /// <summary>Sets the <c>$self</c> document identity URI (OpenAPI 3.2+).</summary>
    /// <param name="self">The self URI.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiDocumentBuilder Self(string self);

    /// <summary>Sets the default JSON Schema dialect for the document's schemas (OpenAPI 3.1+).</summary>
    /// <param name="dialect">The dialect URI.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiDocumentBuilder JsonSchemaDialect(string dialect);

    /// <summary>Adds a server.</summary>
    /// <param name="url">The server URL.</param>
    /// <param name="description">An optional server description.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiDocumentBuilder Server(string url, string? description = null);

    /// <summary>Adds a path and its operations.</summary>
    /// <param name="template">The path template, for example <c>/pets/{id}</c>.</param>
    /// <param name="configure">Configures the path item.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiDocumentBuilder Path(string template, Action<IOpenApiPathItemBuilder> configure);

    /// <summary>Adds a webhook (OpenAPI 3.1+).</summary>
    /// <param name="name">The webhook name.</param>
    /// <param name="configure">Configures the webhook path item.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiDocumentBuilder Webhook(string name, Action<IOpenApiPathItemBuilder> configure);

    /// <summary>Configures the reusable components.</summary>
    /// <param name="configure">Configures the components.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiDocumentBuilder Components(Action<IOpenApiComponentsBuilder> configure);

    /// <summary>Adds a document-level security requirement.</summary>
    /// <param name="scheme">The security scheme name.</param>
    /// <param name="scopes">The required scopes, if any.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiDocumentBuilder Security(string scheme, params string[] scopes);

    /// <summary>Adds a tag.</summary>
    /// <param name="name">The tag name.</param>
    /// <param name="configure">Configures the tag.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiDocumentBuilder Tag(string name, Action<IOpenApiTagBuilder> configure);

    /// <summary>Sets a specification extension (an <c>x-</c> field) on the document root.</summary>
    /// <param name="name">The extension name, including the <c>x-</c> prefix.</param>
    /// <param name="value">The extension value.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiDocumentBuilder Extension(string name, OpenApiNode value);

    /// <summary>Builds the document.</summary>
    /// <returns>The assembled <see cref="OpenApiDocument"/> with its <see cref="OpenApiDocument.SpecVersion"/> set to the target line.</returns>
    OpenApiDocument Build();
}
