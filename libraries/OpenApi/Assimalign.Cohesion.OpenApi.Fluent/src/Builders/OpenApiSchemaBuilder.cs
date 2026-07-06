using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiSchema"/>. Version-gated keywords (for example the schema
/// <c>examples</c> array or <c>const</c>) throw <see cref="OpenApiException"/> when the target line does
/// not support them.
/// </summary>
public sealed class OpenApiSchemaBuilder
{
    private readonly OpenApiSchema _schema = new();
    private readonly OpenApiSpecVersion _version;

    /// <summary>Initializes a new instance of the <see cref="OpenApiSchemaBuilder"/> class.</summary>
    /// <param name="version">The target OpenAPI specification version.</param>
    public OpenApiSchemaBuilder(OpenApiSpecVersion version)
    {
        _version = version;
    }

    /// <summary>Builds the configured <see cref="OpenApiSchema"/>.</summary>
    /// <returns>The configured schema.</returns>
    public OpenApiSchema Build() => _schema;

    /// <summary>Sets the instance type of the schema.</summary>
    /// <param name="type">The instance type.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSchemaBuilder Type(SchemaType type)
    {
        _schema.Type = type;
        return this;
    }

    /// <summary>Adds an instance type, producing a multi-type schema (OpenAPI 3.1+).</summary>
    /// <param name="type">The additional instance type.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSchemaBuilder AddType(SchemaType type)
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

    /// <summary>Marks the schema as nullable.</summary>
    /// <param name="nullable">Whether the value may be null.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSchemaBuilder Nullable(bool nullable = true)
    {
        _schema.Nullable = nullable;
        return this;
    }

    /// <summary>Sets the format modifier.</summary>
    /// <param name="format">The format, for example <c>date-time</c>.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSchemaBuilder Format(string format)
    {
        ArgumentNullException.ThrowIfNull(format);
        _schema.Format = format;
        return this;
    }

    /// <summary>Sets the schema title.</summary>
    /// <param name="title">The title.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSchemaBuilder Title(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        _schema.Title = title;
        return this;
    }

    /// <summary>Sets the schema description.</summary>
    /// <param name="description">The description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSchemaBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _schema.Description = description;
        return this;
    }

    /// <summary>Sets the schema to a reference. Other keywords set alongside require OpenAPI 3.1+.</summary>
    /// <param name="reference">The <c>$ref</c> value, for example <c>#/components/schemas/Pet</c>.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSchemaBuilder Reference(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        _schema.Reference = new OpenApiReference { Ref = reference };
        return this;
    }

    /// <summary>Sets the default value.</summary>
    /// <param name="value">The default value.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSchemaBuilder Default(OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _schema.Default = value;
        return this;
    }

    /// <summary>Adds an enumerated allowed value.</summary>
    /// <param name="value">The allowed value.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSchemaBuilder EnumValue(OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _schema.Enum.Add(value);
        return this;
    }

    /// <summary>Sets the single constant value (OpenAPI 3.1+).</summary>
    /// <param name="value">The constant value.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSchemaBuilder Const(OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.SchemaConst, "The schema 'const' keyword");
        _schema.Const = value;
        return this;
    }

    /// <summary>Adds a schema-level example (OpenAPI 3.1+).</summary>
    /// <param name="value">The example value.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSchemaBuilder Example(OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.SchemaExamples, "The schema 'examples' keyword");
        _schema.Examples.Add(value);
        return this;
    }

    /// <summary>Adds an object property.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="configure">Configures the property schema.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSchemaBuilder Property(string name, Action<OpenApiSchemaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiSchemaBuilder(_version);
        configure(builder);
        _schema.Properties[name] = builder.Build();
        return this;
    }

    /// <summary>Marks properties as required.</summary>
    /// <param name="names">The required property names.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSchemaBuilder Required(params string[] names)
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

    /// <summary>Sets the array item schema.</summary>
    /// <param name="configure">Configures the item schema.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSchemaBuilder Items(Action<OpenApiSchemaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiSchemaBuilder(_version);
        configure(builder);
        _schema.Items = builder.Build();
        return this;
    }

    /// <summary>Adds a schema to the <c>allOf</c> composition.</summary>
    /// <param name="configure">Configures the composed schema.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSchemaBuilder AllOf(Action<OpenApiSchemaBuilder> configure) => Compose(_schema.AllOf, configure);

    /// <summary>Adds a schema to the <c>anyOf</c> composition.</summary>
    /// <param name="configure">Configures the composed schema.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSchemaBuilder AnyOf(Action<OpenApiSchemaBuilder> configure) => Compose(_schema.AnyOf, configure);

    /// <summary>Adds a schema to the <c>oneOf</c> composition.</summary>
    /// <param name="configure">Configures the composed schema.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSchemaBuilder OneOf(Action<OpenApiSchemaBuilder> configure) => Compose(_schema.OneOf, configure);

    /// <summary>Sets additional external documentation for the schema.</summary>
    /// <param name="url">The documentation URL.</param>
    /// <param name="description">An optional description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSchemaBuilder ExternalDocs(string url, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        _schema.ExternalDocs = new OpenApiExternalDocumentation { Url = url, Description = description };
        return this;
    }

    /// <summary>Sets a specification extension (an <c>x-</c> field).</summary>
    /// <param name="name">The extension name, including the <c>x-</c> prefix.</param>
    /// <param name="value">The extension value.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSchemaBuilder Extension(string name, OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        _schema.Extensions[name] = value;
        return this;
    }

    private OpenApiSchemaBuilder Compose(System.Collections.Generic.IList<OpenApiSchema> target, Action<OpenApiSchemaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiSchemaBuilder(_version);
        configure(builder);
        target.Add(builder.Build());
        return this;
    }
}
