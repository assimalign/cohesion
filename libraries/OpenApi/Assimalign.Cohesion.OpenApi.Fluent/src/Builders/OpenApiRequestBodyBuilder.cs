using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiRequestBody"/>.
/// </summary>
public sealed class OpenApiRequestBodyBuilder
{
    private readonly OpenApiRequestBody _body = new();
    private readonly OpenApiSpecVersion _version;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiRequestBodyBuilder"/> class.
    /// </summary>
    /// <param name="version">The OpenAPI specification version the builder targets.</param>
    public OpenApiRequestBodyBuilder(OpenApiSpecVersion version)
    {
        _version = version;
    }

    /// <summary>Builds the configured <see cref="OpenApiRequestBody"/>.</summary>
    /// <returns>The built <see cref="OpenApiRequestBody"/>.</returns>
    public OpenApiRequestBody Build() => _body;

    /// <summary>Sets the request body description.</summary>
    /// <param name="description">The description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiRequestBodyBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _body.Description = description;
        return this;
    }

    /// <summary>Marks the request body as required.</summary>
    /// <param name="required">Whether the body is mandatory.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiRequestBodyBuilder Required(bool required = true)
    {
        _body.Required = required;
        return this;
    }

    /// <summary>Adds a content entry for a media type.</summary>
    /// <param name="mediaType">The media type key, for example <c>application/json</c>.</param>
    /// <param name="configure">Configures the media type.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiRequestBodyBuilder Content(string mediaType, Action<OpenApiMediaTypeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiMediaTypeBuilder(_version);
        configure(builder);
        _body.Content[mediaType] = builder.Build();
        return this;
    }
}
