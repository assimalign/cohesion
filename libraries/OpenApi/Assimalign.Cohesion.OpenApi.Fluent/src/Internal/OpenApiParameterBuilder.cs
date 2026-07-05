using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>The default <see cref="IOpenApiParameterBuilder"/> implementation.</summary>
internal sealed class OpenApiParameterBuilder : IOpenApiParameterBuilder
{
    private readonly OpenApiParameter _parameter;
    private readonly OpenApiSpecVersion _version;

    internal OpenApiParameterBuilder(OpenApiParameter parameter, OpenApiSpecVersion version)
    {
        _parameter = parameter;
        _version = version;
    }

    internal OpenApiParameter Build() => _parameter;

    public IOpenApiParameterBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _parameter.Description = description;
        return this;
    }

    public IOpenApiParameterBuilder Required(bool required = true)
    {
        _parameter.Required = required;
        return this;
    }

    public IOpenApiParameterBuilder Deprecated(bool deprecated = true)
    {
        _parameter.Deprecated = deprecated;
        return this;
    }

    public IOpenApiParameterBuilder Style(ParameterStyle style)
    {
        if (style == ParameterStyle.Cookie)
        {
            OpenApiBuildGuard.Require(_version, OpenApiFeature.ParameterCookieStyle, "The 'cookie' parameter style");
        }

        _parameter.Style = style;
        return this;
    }

    public IOpenApiParameterBuilder Explode(bool explode = true)
    {
        _parameter.Explode = explode;
        return this;
    }

    public IOpenApiParameterBuilder Schema(Action<IOpenApiSchemaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiSchemaBuilder(_version);
        configure(builder);
        _parameter.Schema = builder.Build();
        return this;
    }

    public IOpenApiParameterBuilder Content(string mediaType, Action<IOpenApiMediaTypeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiMediaTypeBuilder(_version);
        configure(builder);
        _parameter.Content[mediaType] = builder.Build();
        return this;
    }

    public IOpenApiParameterBuilder Example(string name, Action<IOpenApiExampleBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiExampleBuilder(_version);
        configure(builder);
        _parameter.Examples[name] = builder.Build();
        return this;
    }
}
