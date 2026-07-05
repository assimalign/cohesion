using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>The default <see cref="IOpenApiPathItemBuilder"/> implementation.</summary>
internal sealed class OpenApiPathItemBuilder : IOpenApiPathItemBuilder
{
    private readonly OpenApiPathItem _item = new();
    private readonly OpenApiSpecVersion _version;

    internal OpenApiPathItemBuilder(OpenApiSpecVersion version)
    {
        _version = version;
    }

    internal OpenApiPathItem Build() => _item;

    public IOpenApiPathItemBuilder Summary(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        _item.Summary = summary;
        return this;
    }

    public IOpenApiPathItemBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _item.Description = description;
        return this;
    }

    public IOpenApiPathItemBuilder Operation(OperationType method, Action<IOpenApiOperationBuilder> configure)
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

    public IOpenApiPathItemBuilder AdditionalOperation(string method, Action<IOpenApiOperationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(configure);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.PathItemAdditionalOperations, "The 'additionalOperations' map");
        var builder = new OpenApiOperationBuilder(_version);
        configure(builder);
        _item.AdditionalOperations[method] = builder.Build();
        return this;
    }

    public IOpenApiPathItemBuilder Parameter(string name, ParameterLocation location, Action<IOpenApiParameterBuilder> configure)
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
