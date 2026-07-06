using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiResponse"/>.
/// </summary>
public sealed class OpenApiResponseBuilder
{
    private readonly OpenApiResponse _response = new();
    private readonly OpenApiSpecVersion _version;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiResponseBuilder"/> class.
    /// </summary>
    /// <param name="version">The OpenAPI specification version that governs feature availability.</param>
    public OpenApiResponseBuilder(OpenApiSpecVersion version)
    {
        _version = version;
    }

    /// <summary>Builds the configured <see cref="OpenApiResponse"/>.</summary>
    /// <returns>The configured <see cref="OpenApiResponse"/>.</returns>
    public OpenApiResponse Build() => _response;

    /// <summary>Sets a short summary of the response (OpenAPI 3.2+).</summary>
    /// <param name="summary">The summary.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiResponseBuilder Summary(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.ResponseSummary, "The Response Object 'summary' field");
        _response.Summary = summary;
        return this;
    }

    /// <summary>Sets the response description.</summary>
    /// <param name="description">The description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiResponseBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _response.Description = description;
        return this;
    }

    /// <summary>Adds a content entry for a media type.</summary>
    /// <param name="mediaType">The media type key, for example <c>application/json</c>.</param>
    /// <param name="configure">Configures the media type.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiResponseBuilder Content(string mediaType, Action<OpenApiMediaTypeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiMediaTypeBuilder(_version);
        configure(builder);
        _response.Content[mediaType] = builder.Build();
        return this;
    }

    /// <summary>Adds a response header.</summary>
    /// <param name="name">The header name.</param>
    /// <param name="configure">Configures the header schema.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiResponseBuilder Header(string name, Action<OpenApiSchemaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiSchemaBuilder(_version);
        configure(builder);
        _response.Headers[name] = new OpenApiHeader { Schema = builder.Build() };
        return this;
    }

    /// <summary>Adds an operation link that can be followed from this response.</summary>
    /// <param name="name">The link name.</param>
    /// <param name="configure">Configures the link.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiResponseBuilder Link(string name, Action<OpenApiLinkBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiLinkBuilder();
        configure(builder);
        _response.Links[name] = builder.Build();
        return this;
    }

    /// <summary>Adds an operation link by reference.</summary>
    /// <param name="name">The link name.</param>
    /// <param name="reference">The <c>$ref</c> value, for example <c>#/components/links/GetUserByUserId</c>.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiResponseBuilder LinkReference(string name, string reference)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(reference);
        _response.Links[name] = new OpenApiLink { Reference = new OpenApiReference { Ref = reference } };
        return this;
    }
}
