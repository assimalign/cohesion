using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>The default <see cref="IOpenApiComponentsBuilder"/> implementation.</summary>
internal sealed class OpenApiComponentsBuilder : IOpenApiComponentsBuilder
{
    private readonly OpenApiComponents _components;
    private readonly OpenApiSpecVersion _version;

    internal OpenApiComponentsBuilder(OpenApiComponents components, OpenApiSpecVersion version)
    {
        _components = components;
        _version = version;
    }

    public IOpenApiComponentsBuilder Schema(string name, Action<IOpenApiSchemaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiSchemaBuilder(_version);
        configure(builder);
        _components.Schemas[name] = builder.Build();
        return this;
    }

    public IOpenApiComponentsBuilder Parameter(string name, string parameterName, ParameterLocation location, Action<IOpenApiParameterBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(parameterName);
        ArgumentNullException.ThrowIfNull(configure);
        var parameter = new OpenApiParameter { Name = parameterName, In = location, Required = location == ParameterLocation.Path };
        var builder = new OpenApiParameterBuilder(parameter, _version);
        configure(builder);
        _components.Parameters[name] = builder.Build();
        return this;
    }

    public IOpenApiComponentsBuilder Response(string name, Action<IOpenApiResponseBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiResponseBuilder(_version);
        configure(builder);
        _components.Responses[name] = builder.Build();
        return this;
    }

    public IOpenApiComponentsBuilder RequestBody(string name, Action<IOpenApiRequestBodyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiRequestBodyBuilder(_version);
        configure(builder);
        _components.RequestBodies[name] = builder.Build();
        return this;
    }

    public IOpenApiComponentsBuilder Example(string name, Action<IOpenApiExampleBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiExampleBuilder(_version);
        configure(builder);
        _components.Examples[name] = builder.Build();
        return this;
    }

    public IOpenApiComponentsBuilder SecurityScheme(string name, Action<IOpenApiSecuritySchemeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiSecuritySchemeBuilder(_version);
        configure(builder);
        _components.SecuritySchemes[name] = builder.Build();
        return this;
    }
}
