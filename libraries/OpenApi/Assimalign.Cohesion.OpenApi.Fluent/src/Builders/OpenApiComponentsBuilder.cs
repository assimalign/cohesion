using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for the <see cref="OpenApiComponents"/> container.
/// </summary>
public sealed class OpenApiComponentsBuilder
{
    private readonly OpenApiComponents _components;
    private readonly OpenApiSpecVersion _version;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiComponentsBuilder"/> class.
    /// </summary>
    /// <param name="components">The components container being configured.</param>
    /// <param name="version">The OpenAPI specification version.</param>
    public OpenApiComponentsBuilder(OpenApiComponents components, OpenApiSpecVersion version)
    {
        _components = components;
        _version = version;
    }

    /// <summary>Adds a reusable schema.</summary>
    /// <param name="name">The component name.</param>
    /// <param name="configure">Configures the schema.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiComponentsBuilder Schema(string name, Action<OpenApiSchemaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiSchemaBuilder(_version);
        configure(builder);
        _components.Schemas[name] = builder.Build();
        return this;
    }

    /// <summary>Adds a reusable parameter.</summary>
    /// <param name="name">The component name.</param>
    /// <param name="parameterName">The parameter name.</param>
    /// <param name="location">The parameter location.</param>
    /// <param name="configure">Configures the parameter.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiComponentsBuilder Parameter(string name, string parameterName, ParameterLocation location, Action<OpenApiParameterBuilder> configure)
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

    /// <summary>Adds a reusable response.</summary>
    /// <param name="name">The component name.</param>
    /// <param name="configure">Configures the response.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiComponentsBuilder Response(string name, Action<OpenApiResponseBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiResponseBuilder(_version);
        configure(builder);
        _components.Responses[name] = builder.Build();
        return this;
    }

    /// <summary>Adds a reusable request body.</summary>
    /// <param name="name">The component name.</param>
    /// <param name="configure">Configures the request body.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiComponentsBuilder RequestBody(string name, Action<OpenApiRequestBodyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiRequestBodyBuilder(_version);
        configure(builder);
        _components.RequestBodies[name] = builder.Build();
        return this;
    }

    /// <summary>Adds a reusable example.</summary>
    /// <param name="name">The component name.</param>
    /// <param name="configure">Configures the example.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiComponentsBuilder Example(string name, Action<OpenApiExampleBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiExampleBuilder(_version);
        configure(builder);
        _components.Examples[name] = builder.Build();
        return this;
    }

    /// <summary>Adds a reusable security scheme.</summary>
    /// <param name="name">The component name.</param>
    /// <param name="configure">Configures the security scheme.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiComponentsBuilder SecurityScheme(string name, Action<OpenApiSecuritySchemeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiSecuritySchemeBuilder(_version);
        configure(builder);
        _components.SecuritySchemes[name] = builder.Build();
        return this;
    }

    /// <summary>Adds a reusable link.</summary>
    /// <param name="name">The component name.</param>
    /// <param name="configure">Configures the link.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiComponentsBuilder Link(string name, Action<OpenApiLinkBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiLinkBuilder();
        configure(builder);
        _components.Links[name] = builder.Build();
        return this;
    }

    /// <summary>Adds a reusable callback.</summary>
    /// <param name="name">The component name.</param>
    /// <param name="configure">Configures the callback.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiComponentsBuilder Callback(string name, Action<OpenApiCallbackBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiCallbackBuilder(_version);
        configure(builder);
        _components.Callbacks[name] = builder.Build();
        return this;
    }
}
