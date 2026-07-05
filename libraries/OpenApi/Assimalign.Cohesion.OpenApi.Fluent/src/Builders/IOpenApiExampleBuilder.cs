namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiExample"/>. The 3.2-only <c>dataValue</c> and
/// <c>serializedValue</c> fields throw <see cref="OpenApiException"/> when the target line is earlier.
/// </summary>
public interface IOpenApiExampleBuilder
{
    /// <summary>Sets a short summary for the example.</summary>
    /// <param name="summary">The summary.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiExampleBuilder Summary(string summary);

    /// <summary>Sets a long description for the example.</summary>
    /// <param name="description">The description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiExampleBuilder Description(string description);

    /// <summary>Sets the embedded literal example value.</summary>
    /// <param name="value">The example value.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiExampleBuilder Value(OpenApiNode value);

    /// <summary>Sets the schema-valid example data structure (OpenAPI 3.2+).</summary>
    /// <param name="value">The example data.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiExampleBuilder DataValue(OpenApiNode value);

    /// <summary>Sets the serialized form of the example (OpenAPI 3.2+).</summary>
    /// <param name="value">The serialized value.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiExampleBuilder SerializedValue(string value);

    /// <summary>Sets a URI identifying an external example.</summary>
    /// <param name="uri">The external value URI.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiExampleBuilder ExternalValue(string uri);
}
