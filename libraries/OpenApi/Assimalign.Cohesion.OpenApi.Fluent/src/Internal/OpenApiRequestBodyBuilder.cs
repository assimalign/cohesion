using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>The default <see cref="IOpenApiRequestBodyBuilder"/> implementation.</summary>
internal sealed class OpenApiRequestBodyBuilder : IOpenApiRequestBodyBuilder
{
    private readonly OpenApiRequestBody _body = new();
    private readonly OpenApiSpecVersion _version;

    internal OpenApiRequestBodyBuilder(OpenApiSpecVersion version)
    {
        _version = version;
    }

    internal OpenApiRequestBody Build() => _body;

    public IOpenApiRequestBodyBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _body.Description = description;
        return this;
    }

    public IOpenApiRequestBodyBuilder Required(bool required = true)
    {
        _body.Required = required;
        return this;
    }

    public IOpenApiRequestBodyBuilder Content(string mediaType, Action<IOpenApiMediaTypeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiMediaTypeBuilder(_version);
        configure(builder);
        _body.Content[mediaType] = builder.Build();
        return this;
    }
}
