using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>The default <see cref="IOpenApiTagBuilder"/> implementation.</summary>
internal sealed class OpenApiTagBuilder : IOpenApiTagBuilder
{
    private readonly OpenApiTag _tag;
    private readonly OpenApiSpecVersion _version;

    internal OpenApiTagBuilder(OpenApiTag tag, OpenApiSpecVersion version)
    {
        _tag = tag;
        _version = version;
    }

    internal OpenApiTag Build() => _tag;

    public IOpenApiTagBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _tag.Description = description;
        return this;
    }

    public IOpenApiTagBuilder Summary(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.TagExtendedMetadata, "The Tag Object 'summary' field");
        _tag.Summary = summary;
        return this;
    }

    public IOpenApiTagBuilder Parent(string parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.TagExtendedMetadata, "The Tag Object 'parent' field");
        _tag.Parent = parent;
        return this;
    }

    public IOpenApiTagBuilder Kind(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.TagExtendedMetadata, "The Tag Object 'kind' field");
        _tag.Kind = kind;
        return this;
    }
}
