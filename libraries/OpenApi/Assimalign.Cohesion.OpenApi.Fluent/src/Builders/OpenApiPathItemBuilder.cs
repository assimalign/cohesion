using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiPathItem"/>.
/// </summary>
public sealed class OpenApiPathItemBuilder
{
    private readonly OpenApiPathItem _item = new();
    private readonly OpenApiSpecVersion _version;

    /// <summary>Initializes a new instance of the <see cref="OpenApiPathItemBuilder"/> class.</summary>
    /// <param name="version">The OpenAPI specification version being targeted.</param>
    public OpenApiPathItemBuilder(OpenApiSpecVersion version)
    {
        _version = version;
    }

    /// <summary>Builds the configured <see cref="OpenApiPathItem"/>.</summary>
    /// <returns>The configured path item.</returns>
    public OpenApiPathItem Build() => _item;

    /// <summary>Sets a summary that applies to all operations on the path.</summary>
    /// <param name="summary">The summary.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiPathItemBuilder Summary(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        _item.Summary = summary;
        return this;
    }

    /// <summary>Sets a description that applies to all operations on the path.</summary>
    /// <param name="description">The description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiPathItemBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _item.Description = description;
        return this;
    }

    /// <summary>Adds an operation for a standard HTTP method.</summary>
    /// <param name="method">The HTTP method. <see cref="OperationType.Query"/> requires OpenAPI 3.2+.</param>
    /// <param name="configure">Configures the operation.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiPathItemBuilder Operation(OperationType method, Action<OpenApiOperationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        if (method == OperationType.Query)
        {
            OpenApiBuildGuard.Require(_version, OpenApiFeature.PathItemQueryOperation, "The 'query' operation");
        }

        var builder = new OpenApiOperationBuilder(_version);
        configure(builder);
        _item.Operations[method] = builder.Build();
        return this;
    }

    /// <summary>Adds an operation for a non-standard HTTP method (OpenAPI 3.2+).</summary>
    /// <param name="method">The method name, in the capitalization to be sent.</param>
    /// <param name="configure">Configures the operation.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiPathItemBuilder AdditionalOperation(string method, Action<OpenApiOperationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(configure);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.PathItemAdditionalOperations, "The 'additionalOperations' map");
        var builder = new OpenApiOperationBuilder(_version);
        configure(builder);
        _item.AdditionalOperations[method] = builder.Build();
        return this;
    }

    /// <summary>Adds a parameter that applies to all operations on the path.</summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="location">The parameter location.</param>
    /// <param name="configure">Configures the parameter.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiPathItemBuilder Parameter(string name, ParameterLocation location, Action<OpenApiParameterBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        if (location == ParameterLocation.Querystring)
        {
            OpenApiBuildGuard.Require(_version, OpenApiFeature.ParameterQuerystringLocation, "The 'querystring' parameter location");
        }

        var parameter = new OpenApiParameter { Name = name, In = location, Required = location == ParameterLocation.Path };
        var builder = new OpenApiParameterBuilder(parameter, _version);
        configure(builder);
        _item.Parameters.Add(builder.Build());
        return this;
    }
}
