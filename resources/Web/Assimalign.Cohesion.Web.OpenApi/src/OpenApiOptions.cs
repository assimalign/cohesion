using System;
using System.Collections.Generic;

using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.OpenApi.Attributes;
using Assimalign.Cohesion.OpenApi.Integration;

namespace Assimalign.Cohesion.Web.OpenApi;

/// <summary>
/// Builder-time options for the application's OpenAPI document: the document-level information, the
/// OpenAPI line it targets, the security schemes and tags it declares, extra endpoint sources, and
/// transformers that edit the finished document. Configure them in the
/// <see cref="OpenApiWebApplicationExtensions.AddOpenApi(IWebApplicationBuilder, Action{OpenApiOptions})"/>
/// callback.
/// </summary>
/// <remarks>
/// <para>
/// Composition is dependency-free: the options are values captured at builder time, with no service
/// container or configuration binding. When the <c>AddOpenApi</c> callback returns, the options become
/// read-only and every mutator throws <see cref="InvalidOperationException"/>, so the document a request
/// receives cannot change underneath it.
/// </para>
/// <para>
/// Security schemes are matched to endpoints by name: declare each authentication scheme an endpoint's
/// authorization uses (for example <c>Bearer</c>) under the same name, and every endpoint whose effective
/// authorization policy authenticates through it, a fallback or named policy included, lists it as a
/// security requirement. A policy that names no scheme authenticates through the application's default
/// authenticate scheme.
/// </para>
/// </remarks>
public sealed class OpenApiOptions
{
    private readonly List<OpenApiSecuritySchemeMetadata> _securitySchemes = [];
    private readonly List<OpenApiTagMetadata> _tags = [];
    private readonly List<IOpenApiEndpointSource> _endpointSources = [];
    private readonly List<Action<OpenApiDocument>> _documentTransformers = [];

    private string _title = GetDefaultTitle();
    private string _apiVersion = "1.0.0";
    private string? _description;
    private OpenApiSpecVersion _specVersion = OpenApiSpecVersion.V3_1;
    private bool _isReadOnly;

    /// <summary>
    /// Initializes options with the defaults: the running application's name as the title, API version
    /// <c>1.0.0</c>, and OpenAPI 3.1.
    /// </summary>
    public OpenApiOptions()
    {
        // Read-only views, so a caller cannot cast the lists back and bypass the read-only state.
        SecuritySchemes = _securitySchemes.AsReadOnly();
        Tags = _tags.AsReadOnly();
    }

    /// <summary>
    /// Gets or sets the API title (the document's required <c>info.title</c>). Defaults to the name of the
    /// running application.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The options are read-only.</exception>
    public string Title
    {
        get => _title;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            ThrowIfReadOnly();

            _title = value;
        }
    }

    /// <summary>
    /// Gets or sets the version of the API (the document's required <c>info.version</c>), which is
    /// distinct from the OpenAPI line in <see cref="SpecVersion"/>. Defaults to <c>1.0.0</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The options are read-only.</exception>
    public string ApiVersion
    {
        get => _apiVersion;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            ThrowIfReadOnly();

            _apiVersion = value;
        }
    }

    /// <summary>
    /// Gets or sets an optional description of the API (<c>info.description</c>), rendered as CommonMark.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are read-only.</exception>
    public string? Description
    {
        get => _description;
        set
        {
            ThrowIfReadOnly();

            _description = value;
        }
    }

    /// <summary>
    /// Gets or sets the OpenAPI line a document endpoint serves unless it names its own. Defaults to
    /// <see cref="OpenApiSpecVersion.V3_1"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="OpenApiSpecVersion"/>.</exception>
    /// <exception cref="InvalidOperationException">The options are read-only.</exception>
    public OpenApiSpecVersion SpecVersion
    {
        get => _specVersion;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The value is not a defined OpenApiSpecVersion.");
            }

            ThrowIfReadOnly();

            _specVersion = value;
        }
    }

    /// <summary>
    /// Gets the security schemes the document declares, in registration order.
    /// </summary>
    public IReadOnlyList<OpenApiSecuritySchemeMetadata> SecuritySchemes { get; }

    /// <summary>
    /// Gets the tags the document declares with their descriptions, in registration order. Tags an
    /// endpoint uses without a declaration are added to the document after these, by name only.
    /// </summary>
    public IReadOnlyList<OpenApiTagMetadata> Tags { get; }

    /// <summary>
    /// Gets the extra endpoint sources composed into the document after the application's routes.
    /// </summary>
    internal IReadOnlyList<IOpenApiEndpointSource> EndpointSources => _endpointSources;

    /// <summary>
    /// Gets the document transformers, in registration order.
    /// </summary>
    internal IReadOnlyList<Action<OpenApiDocument>> DocumentTransformers => _documentTransformers;

    /// <summary>
    /// Declares a security scheme. Its <see cref="OpenApiSecuritySchemeMetadata.Name"/> is the component
    /// name and must equal the name of the authentication scheme it describes, which is how endpoints that
    /// require authorization through that scheme find it.
    /// </summary>
    /// <param name="scheme">The security scheme, for example an <c>http</c> <c>bearer</c> scheme named <c>Bearer</c>.</param>
    /// <returns>The same options, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scheme"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A scheme with the same name is already declared, or the options are read-only.
    /// </exception>
    public OpenApiOptions AddSecurityScheme(OpenApiSecuritySchemeMetadata scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        ThrowIfReadOnly();

        foreach (OpenApiSecuritySchemeMetadata existing in _securitySchemes)
        {
            if (string.Equals(existing.Name, scheme.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"An OpenAPI security scheme named '{scheme.Name}' is already declared.");
            }
        }

        _securitySchemes.Add(scheme);
        return this;
    }

    /// <summary>
    /// Declares a document tag, so the tag an endpoint uses (<c>WithTags</c>) carries a description and,
    /// for OpenAPI 3.2, a summary, parent and kind.
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The same options, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tag"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A tag with the same name is already declared, or the options are read-only.
    /// </exception>
    public OpenApiOptions AddTag(OpenApiTagMetadata tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        ThrowIfReadOnly();

        foreach (OpenApiTagMetadata existing in _tags)
        {
            if (string.Equals(existing.Name, tag.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"An OpenAPI tag named '{tag.Name}' is already declared.");
            }
        }

        _tags.Add(tag);
        return this;
    }

    /// <summary>
    /// Composes another endpoint source into the document after the application's routes, for example the
    /// registry the OpenApi attribute source generator emits for annotated operations the router does not
    /// map.
    /// </summary>
    /// <param name="source">The endpoint source.</param>
    /// <returns>The same options, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The options are read-only.</exception>
    /// <remarks>
    /// The source's operations, schemas, tags and security schemes are added as they are. An operation on
    /// a path and method the routes already describe replaces the route's.
    /// </remarks>
    public OpenApiOptions AddEndpointSource(IOpenApiEndpointSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfReadOnly();

        _endpointSources.Add(source);
        return this;
    }

    /// <summary>
    /// Registers a transformer that edits each document after it is generated, for what endpoint metadata
    /// does not carry: servers, contact and license information, external documentation, or extensions.
    /// </summary>
    /// <param name="transformer">The transformer. It runs once per document build, in registration order.</param>
    /// <returns>The same options, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transformer"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The options are read-only.</exception>
    /// <remarks>
    /// A document endpoint builds its document once, on its first request, and serves that result from
    /// then on, so a transformer runs once per endpoint and must not depend on the request.
    /// </remarks>
    public OpenApiOptions AddDocumentTransformer(Action<OpenApiDocument> transformer)
    {
        ArgumentNullException.ThrowIfNull(transformer);
        ThrowIfReadOnly();

        _documentTransformers.Add(transformer);
        return this;
    }

    /// <summary>
    /// Makes the options read-only. <c>AddOpenApi</c> calls this once its callback returns.
    /// </summary>
    internal void MakeReadOnly() => _isReadOnly = true;

    private void ThrowIfReadOnly()
    {
        if (_isReadOnly)
        {
            throw new InvalidOperationException(
                "OpenAPI options are read-only once AddOpenApi has captured them. Configure the document inside the AddOpenApi callback.");
        }
    }

    private static string GetDefaultTitle()
    {
        string name = AppDomain.CurrentDomain.FriendlyName;

        return string.IsNullOrWhiteSpace(name) ? "API" : name;
    }
}
