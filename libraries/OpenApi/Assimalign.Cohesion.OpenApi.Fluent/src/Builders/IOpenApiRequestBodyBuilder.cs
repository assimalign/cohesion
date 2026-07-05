using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiRequestBody"/>.
/// </summary>
public interface IOpenApiRequestBodyBuilder
{
    /// <summary>Sets the request body description.</summary>
    /// <param name="description">The description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiRequestBodyBuilder Description(string description);

    /// <summary>Marks the request body as required.</summary>
    /// <param name="required">Whether the body is mandatory.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiRequestBodyBuilder Required(bool required = true);

    /// <summary>Adds a content entry for a media type.</summary>
    /// <param name="mediaType">The media type key, for example <c>application/json</c>.</param>
    /// <param name="configure">Configures the media type.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiRequestBodyBuilder Content(string mediaType, Action<IOpenApiMediaTypeBuilder> configure);
}
