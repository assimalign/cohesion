using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>The default <see cref="IOpenApiOperationBuilder"/> implementation.</summary>
internal sealed class OpenApiOperationBuilder : IOpenApiOperationBuilder
{
    private readonly OpenApiOperation _operation = new();
    private readonly OpenApiSpecVersion _version;

    internal OpenApiOperationBuilder(OpenApiSpecVersion version)
    {
        _version = version;
    }

    internal OpenApiOperation Build() => _operation;

    public IOpenApiOperationBuilder OperationId(string operationId)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        _operation.OperationId = operationId;
        return this;
    }

    public IOpenApiOperationBuilder Summary(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        _operation.Summary = summary;
        return this;
    }

    public IOpenApiOperationBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _operation.Description = description;
        return this;
    }

    public IOpenApiOperationBuilder Tag(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        _operation.Tags.Add(tag);
        return this;
    }

    public IOpenApiOperationBuilder Deprecated(bool deprecated = true)
    {
        _operation.Deprecated = deprecated;
        return this;
    }

    public IOpenApiOperationBuilder Parameter(string name, ParameterLocation location, Action<IOpenApiParameterBuilder> configure)
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
        _operation.Parameters.Add(builder.Build());
        return this;
    }

    public IOpenApiOperationBuilder ParameterReference(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        _operation.Parameters.Add(new OpenApiParameter { Reference = new OpenApiReference { Ref = reference } });
        return this;
    }

    public IOpenApiOperationBuilder RequestBody(Action<IOpenApiRequestBodyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiRequestBodyBuilder(_version);
        configure(builder);
        _operation.RequestBody = builder.Build();
        return this;
    }

    public IOpenApiOperationBuilder Response(string statusCode, Action<IOpenApiResponseBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(statusCode);
        ArgumentNullException.ThrowIfNull(configure);
        _operation.Responses ??= new OpenApiResponses();
        var builder = new OpenApiResponseBuilder(_version);
        configure(builder);
        _operation.Responses.Items[statusCode] = builder.Build();
        return this;
    }

    public IOpenApiOperationBuilder Security(string scheme, params string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        var requirement = new OpenApiSecurityRequirement();
        requirement.Schemes[scheme] = new List<string>(scopes ?? []);
        _operation.Security.Add(requirement);
        return this;
    }

    public IOpenApiOperationBuilder Extension(string name, OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        _operation.Extensions[name] = value;
        return this;
    }
}
