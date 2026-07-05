using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for the <see cref="OpenApiComponents"/> container.
/// </summary>
public interface IOpenApiComponentsBuilder
{
    /// <summary>Adds a reusable schema.</summary>
    /// <param name="name">The component name.</param>
    /// <param name="configure">Configures the schema.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiComponentsBuilder Schema(string name, Action<IOpenApiSchemaBuilder> configure);

    /// <summary>Adds a reusable parameter.</summary>
    /// <param name="name">The component name.</param>
    /// <param name="parameterName">The parameter name.</param>
    /// <param name="location">The parameter location.</param>
    /// <param name="configure">Configures the parameter.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiComponentsBuilder Parameter(string name, string parameterName, ParameterLocation location, Action<IOpenApiParameterBuilder> configure);

    /// <summary>Adds a reusable response.</summary>
    /// <param name="name">The component name.</param>
    /// <param name="configure">Configures the response.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiComponentsBuilder Response(string name, Action<IOpenApiResponseBuilder> configure);

    /// <summary>Adds a reusable request body.</summary>
    /// <param name="name">The component name.</param>
    /// <param name="configure">Configures the request body.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiComponentsBuilder RequestBody(string name, Action<IOpenApiRequestBodyBuilder> configure);

    /// <summary>Adds a reusable example.</summary>
    /// <param name="name">The component name.</param>
    /// <param name="configure">Configures the example.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiComponentsBuilder Example(string name, Action<IOpenApiExampleBuilder> configure);

    /// <summary>Adds a reusable security scheme.</summary>
    /// <param name="name">The component name.</param>
    /// <param name="configure">Configures the security scheme.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiComponentsBuilder SecurityScheme(string name, Action<IOpenApiSecuritySchemeBuilder> configure);
}
