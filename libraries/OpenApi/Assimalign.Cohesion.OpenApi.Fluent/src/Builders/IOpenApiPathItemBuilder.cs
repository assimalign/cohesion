using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiPathItem"/>.
/// </summary>
public interface IOpenApiPathItemBuilder
{
    /// <summary>Sets a summary that applies to all operations on the path.</summary>
    /// <param name="summary">The summary.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiPathItemBuilder Summary(string summary);

    /// <summary>Sets a description that applies to all operations on the path.</summary>
    /// <param name="description">The description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiPathItemBuilder Description(string description);

    /// <summary>Adds an operation for a standard HTTP method.</summary>
    /// <param name="method">The HTTP method. <see cref="OperationType.Query"/> requires OpenAPI 3.2+.</param>
    /// <param name="configure">Configures the operation.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiPathItemBuilder Operation(OperationType method, Action<IOpenApiOperationBuilder> configure);

    /// <summary>Adds an operation for a non-standard HTTP method (OpenAPI 3.2+).</summary>
    /// <param name="method">The method name, in the capitalization to be sent.</param>
    /// <param name="configure">Configures the operation.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiPathItemBuilder AdditionalOperation(string method, Action<IOpenApiOperationBuilder> configure);

    /// <summary>Adds a parameter that applies to all operations on the path.</summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="location">The parameter location.</param>
    /// <param name="configure">Configures the parameter.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiPathItemBuilder Parameter(string name, ParameterLocation location, Action<IOpenApiParameterBuilder> configure);
}
