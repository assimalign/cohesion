using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.OpenApi.Integration;
using Assimalign.Cohesion.Web.Authentication;
using Assimalign.Cohesion.Web.Authorization;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Serialization;

namespace Assimalign.Cohesion.Web.OpenApi.Internal;

/// <summary>
/// The application's <see cref="IOpenApiDescriptionProvider"/>: composes the Web endpoint source over the
/// built route table with the extra sources the options register, through
/// <see cref="OpenApiIntegration.CreateProvider(OpenApiDescriptionInfo, IReadOnlyList{IOpenApiEndpointSource})"/>,
/// then runs the document transformers.
/// </summary>
/// <remarks>
/// Every call builds a new document: the result is an ordinary mutable model, and handing out one shared
/// instance would let a caller change what another sees. The document endpoint caches the serialized form
/// of its own build instead. The application's router, serialization registry, authentication service and
/// authorization options are read when a document is built, never earlier, because reading the router
/// builds it and closes the route table.
/// </remarks>
internal sealed class WebOpenApiDescriptionProvider : IOpenApiDescriptionProvider
{
    private readonly IWebApplicationContext _application;
    private readonly OpenApiOptions _options;

    public WebOpenApiDescriptionProvider(IWebApplicationContext application, OpenApiOptions options)
    {
        _application = application;
        _options = options;
    }

    /// <inheritdoc />
    public OpenApiDocument GetDocument(OpenApiSpecVersion version)
    {
        if (!Enum.IsDefined(version))
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "The value is not a defined OpenApiSpecVersion.");
        }

        IRouterFeature routing = _application.Features.OfType<IRouterFeature>().LastOrDefault()
            ?? throw new InvalidOperationException(
                "Routing has not been registered. Call AddRouting() on the web application builder: the OpenAPI document describes the routes it maps.");

        if (!_application.TryGetAuthorizationOptions(out AuthorizationOptions? authorization))
        {
            // Without AddAuthorization the application is described with the defaults AddAuthorization()
            // registers: no fallback policy, so an endpoint without authorization metadata is open, and
            // RequireAuthorization() means an authenticated user. An endpoint that declares a requirement is
            // therefore never described as open; without the middleware it fails at dispatch instead of running.
            authorization = new AuthorizationOptions();
        }

        WebOpenApiEndpointSource routes = new(
            routing.Router,
            _options,
            _application.Features.OfType<IHttpContentSerializationFeature>().LastOrDefault(),
            authorization,
            _application.Features.OfType<IAuthenticationService>().LastOrDefault()?.DefaultAuthenticateScheme,
            version);

        List<IOpenApiEndpointSource> sources = [routes, .. _options.EndpointSources];

        OpenApiDescriptionInfo info = new()
        {
            Title = _options.Title,
            ApiVersion = _options.ApiVersion,
            Description = _options.Description
        };

        OpenApiDocument document = OpenApiIntegration.CreateProvider(info, sources).GetDocument(version);

        foreach (Action<OpenApiDocument> transformer in _options.DocumentTransformers)
        {
            transformer.Invoke(document);
        }

        return document;
    }
}
