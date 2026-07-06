using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiTag"/>. The 3.2-only <c>summary</c>, <c>parent</c>, and
/// <c>kind</c> fields throw <see cref="OpenApiException"/> when the target line is earlier.
/// </summary>
public sealed class OpenApiTagBuilder
{
    private readonly OpenApiTag _tag;
    private readonly OpenApiSpecVersion _version;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiTagBuilder"/> class.
    /// </summary>
    /// <param name="tag">The tag being built.</param>
    /// <param name="version">The target OpenAPI specification version.</param>
    public OpenApiTagBuilder(OpenApiTag tag, OpenApiSpecVersion version)
    {
        _tag = tag;
        _version = version;
    }

    /// <summary>Builds and returns the configured <see cref="OpenApiTag"/>.</summary>
    /// <returns>The configured tag.</returns>
    public OpenApiTag Build() => _tag;

    /// <summary>Sets the tag description.</summary>
    /// <param name="description">The description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiTagBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _tag.Description = description;
        return this;
    }

    /// <summary>Sets a short summary of the tag (OpenAPI 3.2+).</summary>
    /// <param name="summary">The summary.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiTagBuilder Summary(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.TagExtendedMetadata, "The Tag Object 'summary' field");
        _tag.Summary = summary;
        return this;
    }

    /// <summary>Sets the parent tag name, forming a hierarchy (OpenAPI 3.2+).</summary>
    /// <param name="parent">The parent tag name.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiTagBuilder Parent(string parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.TagExtendedMetadata, "The Tag Object 'parent' field");
        _tag.Parent = parent;
        return this;
    }

    /// <summary>Sets the machine-readable tag kind (OpenAPI 3.2+).</summary>
    /// <param name="kind">The tag kind, for example <c>nav</c>.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiTagBuilder Kind(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.TagExtendedMetadata, "The Tag Object 'kind' field");
        _tag.Kind = kind;
        return this;
    }

    /// <summary>Sets additional external documentation for the tag.</summary>
    /// <param name="url">The documentation URL.</param>
    /// <param name="description">An optional description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiTagBuilder ExternalDocs(string url, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        _tag.ExternalDocs = new OpenApiExternalDocumentation { Url = url, Description = description };
        return this;
    }
}
