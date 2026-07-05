using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiExample"/>. The 3.2-only <c>dataValue</c> and
/// <c>serializedValue</c> fields throw <see cref="OpenApiException"/> when the target line is earlier.
/// </summary>
public sealed class OpenApiExampleBuilder
{
    private readonly OpenApiExample _example = new();
    private readonly OpenApiSpecVersion _version;

    /// <summary>Initializes a new instance of the <see cref="OpenApiExampleBuilder"/> class.</summary>
    /// <param name="version">The OpenAPI specification version the builder targets.</param>
    public OpenApiExampleBuilder(OpenApiSpecVersion version)
    {
        _version = version;
    }

    /// <summary>Builds the configured <see cref="OpenApiExample"/>.</summary>
    /// <returns>The built <see cref="OpenApiExample"/>.</returns>
    public OpenApiExample Build() => _example;

    /// <summary>Sets a short summary for the example.</summary>
    /// <param name="summary">The summary.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiExampleBuilder Summary(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        _example.Summary = summary;
        return this;
    }

    /// <summary>Sets a long description for the example.</summary>
    /// <param name="description">The description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiExampleBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _example.Description = description;
        return this;
    }

    /// <summary>Sets the embedded literal example value.</summary>
    /// <param name="value">The example value.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiExampleBuilder Value(OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _example.Value = value;
        return this;
    }

    /// <summary>Sets the schema-valid example data structure (OpenAPI 3.2+).</summary>
    /// <param name="value">The example data.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiExampleBuilder DataValue(OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.ExampleDataAndSerializedValues, "The Example Object 'dataValue' field");
        _example.DataValue = value;
        return this;
    }

    /// <summary>Sets the serialized form of the example (OpenAPI 3.2+).</summary>
    /// <param name="value">The serialized value.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiExampleBuilder SerializedValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.ExampleDataAndSerializedValues, "The Example Object 'serializedValue' field");
        _example.SerializedValue = value;
        return this;
    }

    /// <summary>Sets a URI identifying an external example.</summary>
    /// <param name="uri">The external value URI.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiExampleBuilder ExternalValue(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        _example.ExternalValue = uri;
        return this;
    }
}
