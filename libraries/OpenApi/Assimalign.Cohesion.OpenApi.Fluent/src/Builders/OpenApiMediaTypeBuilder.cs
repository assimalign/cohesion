using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiMediaType"/> entry in a content map.
/// </summary>
public sealed class OpenApiMediaTypeBuilder
{
    private readonly OpenApiMediaType _media = new();
    private readonly OpenApiSpecVersion _version;

    /// <summary>Initializes a new instance of the <see cref="OpenApiMediaTypeBuilder"/> class.</summary>
    /// <param name="version">The OpenAPI specification version being targeted.</param>
    public OpenApiMediaTypeBuilder(OpenApiSpecVersion version)
    {
        _version = version;
    }

    /// <summary>Builds the configured <see cref="OpenApiMediaType"/>.</summary>
    /// <returns>The configured <see cref="OpenApiMediaType"/>.</returns>
    public OpenApiMediaType Build() => _media;

    /// <summary>Sets the media-type schema.</summary>
    /// <param name="configure">Configures the schema.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiMediaTypeBuilder Schema(Action<OpenApiSchemaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiSchemaBuilder(_version);
        configure(builder);
        _media.Schema = builder.Build();
        return this;
    }

    /// <summary>Sets the media-type schema to a reference.</summary>
    /// <param name="reference">The <c>$ref</c> value.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiMediaTypeBuilder SchemaReference(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        _media.Schema = new OpenApiSchema { Reference = new OpenApiReference { Ref = reference } };
        return this;
    }

    /// <summary>Sets a single example of the media type.</summary>
    /// <param name="value">The example value.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiMediaTypeBuilder Example(OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _media.Example = value;
        return this;
    }

    /// <summary>Adds a named example of the media type.</summary>
    /// <param name="name">The example name.</param>
    /// <param name="configure">Configures the example.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiMediaTypeBuilder Example(string name, Action<OpenApiExampleBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiExampleBuilder(_version);
        configure(builder);
        _media.Examples[name] = builder.Build();
        return this;
    }
}
