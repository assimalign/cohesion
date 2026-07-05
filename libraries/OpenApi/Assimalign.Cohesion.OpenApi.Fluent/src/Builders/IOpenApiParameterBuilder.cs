using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiParameter"/>.
/// </summary>
public interface IOpenApiParameterBuilder
{
    /// <summary>Sets the parameter description.</summary>
    /// <param name="description">The description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiParameterBuilder Description(string description);

    /// <summary>Marks the parameter as required.</summary>
    /// <param name="required">Whether the parameter is mandatory.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiParameterBuilder Required(bool required = true);

    /// <summary>Marks the parameter as deprecated.</summary>
    /// <param name="deprecated">Whether the parameter is deprecated.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiParameterBuilder Deprecated(bool deprecated = true);

    /// <summary>Sets the parameter serialization style.</summary>
    /// <param name="style">The style.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiParameterBuilder Style(ParameterStyle style);

    /// <summary>Sets whether array or object values explode into separate parameters.</summary>
    /// <param name="explode">Whether to explode.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiParameterBuilder Explode(bool explode = true);

    /// <summary>Sets the parameter schema.</summary>
    /// <param name="configure">Configures the schema.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiParameterBuilder Schema(Action<IOpenApiSchemaBuilder> configure);

    /// <summary>Adds a content entry describing the parameter.</summary>
    /// <param name="mediaType">The media type key, for example <c>application/json</c>.</param>
    /// <param name="configure">Configures the media type.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiParameterBuilder Content(string mediaType, Action<IOpenApiMediaTypeBuilder> configure);

    /// <summary>Adds a named example of the parameter value.</summary>
    /// <param name="name">The example name.</param>
    /// <param name="configure">Configures the example.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiParameterBuilder Example(string name, Action<IOpenApiExampleBuilder> configure);
}
