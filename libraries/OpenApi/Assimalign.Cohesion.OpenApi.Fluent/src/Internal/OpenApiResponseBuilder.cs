using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>The default <see cref="IOpenApiResponseBuilder"/> implementation.</summary>
internal sealed class OpenApiResponseBuilder : IOpenApiResponseBuilder
{
    private readonly OpenApiResponse _response = new();
    private readonly OpenApiSpecVersion _version;

    internal OpenApiResponseBuilder(OpenApiSpecVersion version)
    {
        _version = version;
    }

    internal OpenApiResponse Build() => _response;

    public IOpenApiResponseBuilder Summary(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.ResponseSummary, "The Response Object 'summary' field");
        _response.Summary = summary;
        return this;
    }

    public IOpenApiResponseBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _response.Description = description;
        return this;
    }

    public IOpenApiResponseBuilder Content(string mediaType, Action<IOpenApiMediaTypeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiMediaTypeBuilder(_version);
        configure(builder);
        _response.Content[mediaType] = builder.Build();
        return this;
    }

    public IOpenApiResponseBuilder Header(string name, Action<IOpenApiSchemaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiSchemaBuilder(_version);
        configure(builder);
        _response.Headers[name] = new OpenApiHeader { Schema = builder.Build() };
        return this;
    }
}
