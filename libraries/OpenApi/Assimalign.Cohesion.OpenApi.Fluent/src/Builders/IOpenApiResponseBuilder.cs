using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiResponse"/>.
/// </summary>
public interface IOpenApiResponseBuilder
{
    /// <summary>Sets a short summary of the response (OpenAPI 3.2+).</summary>
    /// <param name="summary">The summary.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiResponseBuilder Summary(string summary);

    /// <summary>Sets the response description.</summary>
    /// <param name="description">The description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiResponseBuilder Description(string description);

    /// <summary>Adds a content entry for a media type.</summary>
    /// <param name="mediaType">The media type key, for example <c>application/json</c>.</param>
    /// <param name="configure">Configures the media type.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiResponseBuilder Content(string mediaType, Action<IOpenApiMediaTypeBuilder> configure);

    /// <summary>Adds a response header.</summary>
    /// <param name="name">The header name.</param>
    /// <param name="configure">Configures the header schema.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiResponseBuilder Header(string name, Action<IOpenApiSchemaBuilder> configure);
}
