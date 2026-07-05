using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiSchema"/>. Version-gated keywords (for example the schema
/// <c>examples</c> array or <c>const</c>) throw <see cref="OpenApiException"/> when the target line does
/// not support them.
/// </summary>
public interface IOpenApiSchemaBuilder
{
    /// <summary>Sets the instance type of the schema.</summary>
    /// <param name="type">The instance type.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSchemaBuilder Type(SchemaType type);

    /// <summary>Adds an instance type, producing a multi-type schema (OpenAPI 3.1+).</summary>
    /// <param name="type">The additional instance type.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSchemaBuilder AddType(SchemaType type);

    /// <summary>Marks the schema as nullable.</summary>
    /// <param name="nullable">Whether the value may be null.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSchemaBuilder Nullable(bool nullable = true);

    /// <summary>Sets the format modifier.</summary>
    /// <param name="format">The format, for example <c>date-time</c>.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSchemaBuilder Format(string format);

    /// <summary>Sets the schema title.</summary>
    /// <param name="title">The title.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSchemaBuilder Title(string title);

    /// <summary>Sets the schema description.</summary>
    /// <param name="description">The description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSchemaBuilder Description(string description);

    /// <summary>Sets the schema to a reference. Other keywords set alongside require OpenAPI 3.1+.</summary>
    /// <param name="reference">The <c>$ref</c> value, for example <c>#/components/schemas/Pet</c>.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSchemaBuilder Reference(string reference);

    /// <summary>Sets the default value.</summary>
    /// <param name="value">The default value.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSchemaBuilder Default(OpenApiNode value);

    /// <summary>Adds an enumerated allowed value.</summary>
    /// <param name="value">The allowed value.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSchemaBuilder EnumValue(OpenApiNode value);

    /// <summary>Sets the single constant value (OpenAPI 3.1+).</summary>
    /// <param name="value">The constant value.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSchemaBuilder Const(OpenApiNode value);

    /// <summary>Adds a schema-level example (OpenAPI 3.1+).</summary>
    /// <param name="value">The example value.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSchemaBuilder Example(OpenApiNode value);

    /// <summary>Adds an object property.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="configure">Configures the property schema.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSchemaBuilder Property(string name, Action<IOpenApiSchemaBuilder> configure);

    /// <summary>Marks properties as required.</summary>
    /// <param name="names">The required property names.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSchemaBuilder Required(params string[] names);

    /// <summary>Sets the array item schema.</summary>
    /// <param name="configure">Configures the item schema.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSchemaBuilder Items(Action<IOpenApiSchemaBuilder> configure);

    /// <summary>Adds a schema to the <c>allOf</c> composition.</summary>
    /// <param name="configure">Configures the composed schema.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSchemaBuilder AllOf(Action<IOpenApiSchemaBuilder> configure);

    /// <summary>Adds a schema to the <c>anyOf</c> composition.</summary>
    /// <param name="configure">Configures the composed schema.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSchemaBuilder AnyOf(Action<IOpenApiSchemaBuilder> configure);

    /// <summary>Adds a schema to the <c>oneOf</c> composition.</summary>
    /// <param name="configure">Configures the composed schema.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSchemaBuilder OneOf(Action<IOpenApiSchemaBuilder> configure);

    /// <summary>Sets a specification extension (an <c>x-</c> field).</summary>
    /// <param name="name">The extension name, including the <c>x-</c> prefix.</param>
    /// <param name="value">The extension value.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSchemaBuilder Extension(string name, OpenApiNode value);
}
