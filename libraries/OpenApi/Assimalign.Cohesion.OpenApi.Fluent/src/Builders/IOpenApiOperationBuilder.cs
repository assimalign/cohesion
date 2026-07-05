using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiOperation"/>.
/// </summary>
public interface IOpenApiOperationBuilder
{
    /// <summary>Sets the operation identifier.</summary>
    /// <param name="operationId">The unique operation identifier.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiOperationBuilder OperationId(string operationId);

    /// <summary>Sets a short summary of the operation.</summary>
    /// <param name="summary">The summary.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiOperationBuilder Summary(string summary);

    /// <summary>Sets a verbose description of the operation.</summary>
    /// <param name="description">The description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiOperationBuilder Description(string description);

    /// <summary>Adds a grouping tag to the operation.</summary>
    /// <param name="tag">The tag name.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiOperationBuilder Tag(string tag);

    /// <summary>Marks the operation as deprecated.</summary>
    /// <param name="deprecated">Whether the operation is deprecated.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiOperationBuilder Deprecated(bool deprecated = true);

    /// <summary>Adds a parameter to the operation.</summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="location">The parameter location.</param>
    /// <param name="configure">Configures the parameter.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiOperationBuilder Parameter(string name, ParameterLocation location, Action<IOpenApiParameterBuilder> configure);

    /// <summary>Adds a parameter to the operation by reference.</summary>
    /// <param name="reference">The <c>$ref</c> value, for example <c>#/components/parameters/id</c>.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiOperationBuilder ParameterReference(string reference);

    /// <summary>Sets the request body.</summary>
    /// <param name="configure">Configures the request body.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiOperationBuilder RequestBody(Action<IOpenApiRequestBodyBuilder> configure);

    /// <summary>Adds a response for a status code, range, or <c>default</c>.</summary>
    /// <param name="statusCode">The response key.</param>
    /// <param name="configure">Configures the response.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiOperationBuilder Response(string statusCode, Action<IOpenApiResponseBuilder> configure);

    /// <summary>Adds a security requirement for this operation.</summary>
    /// <param name="scheme">The security scheme name.</param>
    /// <param name="scopes">The required scopes, if any.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiOperationBuilder Security(string scheme, params string[] scopes);

    /// <summary>Sets a specification extension (an <c>x-</c> field).</summary>
    /// <param name="name">The extension name, including the <c>x-</c> prefix.</param>
    /// <param name="value">The extension value.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiOperationBuilder Extension(string name, OpenApiNode value);
}
