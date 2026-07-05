namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiTag"/>. The 3.2-only <c>summary</c>, <c>parent</c>, and
/// <c>kind</c> fields throw <see cref="OpenApiException"/> when the target line is earlier.
/// </summary>
public interface IOpenApiTagBuilder
{
    /// <summary>Sets the tag description.</summary>
    /// <param name="description">The description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiTagBuilder Description(string description);

    /// <summary>Sets a short summary of the tag (OpenAPI 3.2+).</summary>
    /// <param name="summary">The summary.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiTagBuilder Summary(string summary);

    /// <summary>Sets the parent tag name, forming a hierarchy (OpenAPI 3.2+).</summary>
    /// <param name="parent">The parent tag name.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiTagBuilder Parent(string parent);

    /// <summary>Sets the machine-readable tag kind (OpenAPI 3.2+).</summary>
    /// <param name="kind">The tag kind, for example <c>nav</c>.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiTagBuilder Kind(string kind);
}
