using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiParameter"/>.
/// </summary>
public sealed class OpenApiParameterBuilder
{
    private readonly OpenApiParameter _parameter;
    private readonly OpenApiSpecVersion _version;

    /// <summary>Initializes a new instance of the <see cref="OpenApiParameterBuilder"/> class.</summary>
    /// <param name="parameter">The parameter being configured.</param>
    /// <param name="version">The OpenAPI specification version.</param>
    public OpenApiParameterBuilder(OpenApiParameter parameter, OpenApiSpecVersion version)
    {
        _parameter = parameter;
        _version = version;
    }

    /// <summary>Builds the configured <see cref="OpenApiParameter"/>.</summary>
    /// <returns>The configured parameter.</returns>
    public OpenApiParameter Build() => _parameter;

    /// <summary>Sets the parameter description.</summary>
    /// <param name="description">The description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiParameterBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _parameter.Description = description;
        return this;
    }

    /// <summary>Marks the parameter as required.</summary>
    /// <param name="required">Whether the parameter is mandatory.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiParameterBuilder Required(bool required = true)
    {
        _parameter.Required = required;
        return this;
    }

    /// <summary>Marks the parameter as deprecated.</summary>
    /// <param name="deprecated">Whether the parameter is deprecated.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiParameterBuilder Deprecated(bool deprecated = true)
    {
        _parameter.Deprecated = deprecated;
        return this;
    }

    /// <summary>Sets the parameter serialization style.</summary>
    /// <param name="style">The style.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiParameterBuilder Style(ParameterStyle style)
    {
        if (style == ParameterStyle.Cookie)
        {
            OpenApiBuildGuard.Require(_version, OpenApiFeature.ParameterCookieStyle, "The 'cookie' parameter style");
        }

        _parameter.Style = style;
        return this;
    }

    /// <summary>Sets whether array or object values explode into separate parameters.</summary>
    /// <param name="explode">Whether to explode.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiParameterBuilder Explode(bool explode = true)
    {
        _parameter.Explode = explode;
        return this;
    }

    /// <summary>Sets the parameter schema.</summary>
    /// <param name="configure">Configures the schema.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiParameterBuilder Schema(Action<OpenApiSchemaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiSchemaBuilder(_version);
        configure(builder);
        _parameter.Schema = builder.Build();
        return this;
    }

    /// <summary>Adds a content entry describing the parameter.</summary>
    /// <param name="mediaType">The media type key, for example <c>application/json</c>.</param>
    /// <param name="configure">Configures the media type.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiParameterBuilder Content(string mediaType, Action<OpenApiMediaTypeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiMediaTypeBuilder(_version);
        configure(builder);
        _parameter.Content[mediaType] = builder.Build();
        return this;
    }

    /// <summary>Adds a named example of the parameter value.</summary>
    /// <param name="name">The example name.</param>
    /// <param name="configure">Configures the example.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiParameterBuilder Example(string name, Action<OpenApiExampleBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiExampleBuilder(_version);
        configure(builder);
        _parameter.Examples[name] = builder.Build();
        return this;
    }
}
