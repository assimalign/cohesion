using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>The default <see cref="IOpenApiSchemaBuilder"/> implementation, mutating a single <see cref="OpenApiSchema"/>.</summary>
internal sealed class OpenApiSchemaBuilder : IOpenApiSchemaBuilder
{
    private readonly OpenApiSchema _schema = new();
    private readonly OpenApiSpecVersion _version;

    internal OpenApiSchemaBuilder(OpenApiSpecVersion version)
    {
        _version = version;
    }

    internal OpenApiSchema Build() => _schema;

    public IOpenApiSchemaBuilder Type(SchemaType type)
    {
        _schema.Type = type;
        return this;
    }

    public IOpenApiSchemaBuilder AddType(SchemaType type)
    {
        if (_schema.Types.Count > 0)
        {
            OpenApiBuildGuard.Require(_version, OpenApiFeature.SchemaTypeArray, "A multi-type schema");
        }

        if (!_schema.Types.Contains(type))
        {
            _schema.Types.Add(type);
        }

        return this;
    }

    public IOpenApiSchemaBuilder Nullable(bool nullable = true)
    {
        _schema.Nullable = nullable;
        return this;
    }

    public IOpenApiSchemaBuilder Format(string format)
    {
        ArgumentNullException.ThrowIfNull(format);
        _schema.Format = format;
        return this;
    }

    public IOpenApiSchemaBuilder Title(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        _schema.Title = title;
        return this;
    }

    public IOpenApiSchemaBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _schema.Description = description;
        return this;
    }

    public IOpenApiSchemaBuilder Reference(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        _schema.Reference = new OpenApiReference { Ref = reference };
        return this;
    }

    public IOpenApiSchemaBuilder Default(OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _schema.Default = value;
        return this;
    }

    public IOpenApiSchemaBuilder EnumValue(OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _schema.Enum.Add(value);
        return this;
    }

    public IOpenApiSchemaBuilder Const(OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.SchemaConst, "The schema 'const' keyword");
        _schema.Const = value;
        return this;
    }

    public IOpenApiSchemaBuilder Example(OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.SchemaExamples, "The schema 'examples' keyword");
        _schema.Examples.Add(value);
        return this;
    }

    public IOpenApiSchemaBuilder Property(string name, Action<IOpenApiSchemaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiSchemaBuilder(_version);
        configure(builder);
        _schema.Properties[name] = builder.Build();
        return this;
    }

    public IOpenApiSchemaBuilder Required(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (var name in names)
        {
            if (!_schema.Required.Contains(name))
            {
                _schema.Required.Add(name);
            }
        }

        return this;
    }

    public IOpenApiSchemaBuilder Items(Action<IOpenApiSchemaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiSchemaBuilder(_version);
        configure(builder);
        _schema.Items = builder.Build();
        return this;
    }

    public IOpenApiSchemaBuilder AllOf(Action<IOpenApiSchemaBuilder> configure) => Compose(_schema.AllOf, configure);

    public IOpenApiSchemaBuilder AnyOf(Action<IOpenApiSchemaBuilder> configure) => Compose(_schema.AnyOf, configure);

    public IOpenApiSchemaBuilder OneOf(Action<IOpenApiSchemaBuilder> configure) => Compose(_schema.OneOf, configure);

    public IOpenApiSchemaBuilder Extension(string name, OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        _schema.Extensions[name] = value;
        return this;
    }

    private IOpenApiSchemaBuilder Compose(System.Collections.Generic.IList<OpenApiSchema> target, Action<IOpenApiSchemaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiSchemaBuilder(_version);
        configure(builder);
        target.Add(builder.Build());
        return this;
    }
}
