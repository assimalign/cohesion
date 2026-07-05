using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>The default <see cref="IOpenApiMediaTypeBuilder"/> implementation.</summary>
internal sealed class OpenApiMediaTypeBuilder : IOpenApiMediaTypeBuilder
{
    private readonly OpenApiMediaType _media = new();
    private readonly OpenApiSpecVersion _version;

    internal OpenApiMediaTypeBuilder(OpenApiSpecVersion version)
    {
        _version = version;
    }

    internal OpenApiMediaType Build() => _media;

    public IOpenApiMediaTypeBuilder Schema(Action<IOpenApiSchemaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiSchemaBuilder(_version);
        configure(builder);
        _media.Schema = builder.Build();
        return this;
    }

    public IOpenApiMediaTypeBuilder SchemaReference(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        _media.Schema = new OpenApiSchema { Reference = new OpenApiReference { Ref = reference } };
        return this;
    }

    public IOpenApiMediaTypeBuilder Example(OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _media.Example = value;
        return this;
    }

    public IOpenApiMediaTypeBuilder Example(string name, Action<IOpenApiExampleBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiExampleBuilder(_version);
        configure(builder);
        _media.Examples[name] = builder.Build();
        return this;
    }
}
