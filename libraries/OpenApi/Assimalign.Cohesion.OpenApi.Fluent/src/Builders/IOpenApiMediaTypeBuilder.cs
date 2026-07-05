using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiMediaType"/> entry in a content map.
/// </summary>
public interface IOpenApiMediaTypeBuilder
{
    /// <summary>Sets the media-type schema.</summary>
    /// <param name="configure">Configures the schema.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiMediaTypeBuilder Schema(Action<IOpenApiSchemaBuilder> configure);

    /// <summary>Sets the media-type schema to a reference.</summary>
    /// <param name="reference">The <c>$ref</c> value.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiMediaTypeBuilder SchemaReference(string reference);

    /// <summary>Sets a single example of the media type.</summary>
    /// <param name="value">The example value.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiMediaTypeBuilder Example(OpenApiNode value);

    /// <summary>Adds a named example of the media type.</summary>
    /// <param name="name">The example name.</param>
    /// <param name="configure">Configures the example.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiMediaTypeBuilder Example(string name, Action<IOpenApiExampleBuilder> configure);
}
