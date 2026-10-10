using System;
using System.Linq;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.OpenApi.Integration;
using Assimalign.Cohesion.Web.OpenApi.Internal;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.OpenApi;

/// <summary>
/// Pipeline-time mapping and reading of the application's OpenAPI document.
/// </summary>
/// <remarks>
/// <para>
/// <c>builder.Services.AddOpenApi(...)</c>, a component integration the application's compilation receives
/// (<see cref="OpenApiComponents"/>, owner decision 34), registers the document options as an application
/// feature, an <see cref="IHttpFeature"/> singleton; <c>MapOpenApi</c> maps
/// a <c>GET</c> route that serves the document. The document describes the application's routes from the
/// metadata they carry: the source-generated parameter and response descriptions of typed endpoints, the
/// description verbs (<c>WithTags</c>, <c>WithSummary</c>, <c>WithDescription</c>,
/// <c>ExcludeFromDescription</c>), request and response schemas from the application's source-generated
/// System.Text.Json contracts, and security requirements from each endpoint's effective authorization
/// policy: the one <c>UseAuthorization</c> applies, computed by Web.Authorization from the endpoint's
/// authorization metadata and the application's registered default, fallback and named policies. Nothing
/// is discovered by reflection, so the document is generated the same way under NativeAOT.
/// </para>
/// <para>
/// Composition is dependency-free: this package takes no service-container, configuration-binding or hosting
/// reference.
/// </para>
/// </remarks>
public static class OpenApiWebApplicationExtensions
{
    /// <summary>
    /// The route pattern <c>MapOpenApi()</c> serves the document at when none is given.
    /// </summary>
    public const string DefaultDocumentPattern = "/openapi/v1.json";

    extension<TBuilder>(TBuilder builder) where TBuilder : IWebApplicationPipelineBuilder, IWebApplication
    {
        /// <summary>
        /// Maps a <c>GET</c> route that serves the application's OpenAPI document, for the OpenAPI line the
        /// options name (<see cref="OpenApiOptions.SpecVersion"/>).
        /// </summary>
        /// <param name="pattern">
        /// The route pattern. A pattern ending in <c>.yaml</c> or <c>.yml</c> serves YAML
        /// (<c>application/yaml</c>); any other serves JSON (<c>application/json</c>).
        /// </param>
        /// <returns>The document route's builder, for attaching endpoint metadata such as an authorization requirement.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="pattern"/> is <see langword="null"/> or empty.</exception>
        /// <exception cref="InvalidOperationException">
        /// OpenAPI or routing has not been registered (call <c>builder.Services.AddOpenApi</c> and <c>AddRouting</c>), or the
        /// route table has already been built.
        /// </exception>
        /// <remarks>
        /// <para>
        /// The document is built on the route's first request, after the route table is closed, and the
        /// serialized bytes are reused for every later request with a strong <c>ETag</c>; a matching
        /// <c>If-None-Match</c> is answered <c>304</c>. An endpoint that cannot be described (a body or
        /// result type no registered serializer covers) fails that request with an
        /// <see cref="InvalidOperationException"/> naming the endpoint, which reaches the pipeline's
        /// exception boundary.
        /// </para>
        /// <para>
        /// The document route is itself excluded from the description. Requests reach it only through
        /// <c>UseRouting</c>, like every route.
        /// </para>
        /// </remarks>
        public IRouterRouteBuilder MapOpenApi(string pattern = DefaultDocumentPattern)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrEmpty(pattern);

            return builder.MapOpenApi(pattern, GetFeature(builder.Context).Options.SpecVersion);
        }

        /// <summary>
        /// Maps a <c>GET</c> route that serves the application's OpenAPI document for a given OpenAPI line,
        /// for example a 3.0 rendition beside the 3.1 default for tools that do not read 3.1 yet.
        /// </summary>
        /// <param name="pattern">
        /// The route pattern. A pattern ending in <c>.yaml</c> or <c>.yml</c> serves YAML
        /// (<c>application/yaml</c>); any other serves JSON (<c>application/json</c>).
        /// </param>
        /// <param name="specVersion">The OpenAPI line the served document targets.</param>
        /// <returns>The document route's builder, for attaching endpoint metadata such as an authorization requirement.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="pattern"/> is <see langword="null"/> or empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="specVersion"/> is not a defined <see cref="OpenApiSpecVersion"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// OpenAPI or routing has not been registered (call <c>builder.Services.AddOpenApi</c> and <c>AddRouting</c>), or the
        /// route table has already been built.
        /// </exception>
        public IRouterRouteBuilder MapOpenApi(string pattern, OpenApiSpecVersion specVersion)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrEmpty(pattern);

            if (!Enum.IsDefined(specVersion))
            {
                throw new ArgumentOutOfRangeException(nameof(specVersion), specVersion, "The value is not a defined OpenApiSpecVersion.");
            }

            OpenApiDocumentFeature feature = GetFeature(builder.Context);
            OpenApiDocumentEndpoint endpoint = new(
                new WebOpenApiDescriptionProvider(builder.Context, feature.Options),
                specVersion,
                GetFormat(pattern));

            return builder.Map(HttpMethod.Get, pattern, endpoint.InvokeAsync).ExcludeFromDescription();
        }
    }

    extension(IWebApplication application)
    {
        /// <summary>
        /// Gets a description provider that builds the application's OpenAPI document on demand, for tools
        /// and tests that want the document model rather than its HTTP representation.
        /// </summary>
        /// <returns>A provider whose <see cref="IOpenApiDescriptionProvider.GetDocument"/> builds a new document per call.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="application"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">OpenAPI has not been registered (call <c>builder.Services.AddOpenApi</c>).</exception>
        /// <remarks>
        /// Building a document reads the application's router, which builds it and closes the route table,
        /// so call <c>GetDocument</c> after the application has mapped its endpoints, typically after it
        /// started. Each call describes the route table as it is and runs the document transformers.
        /// </remarks>
        public IOpenApiDescriptionProvider GetOpenApiDescriptionProvider()
        {
            ArgumentNullException.ThrowIfNull(application);

            return new WebOpenApiDescriptionProvider(application.Context, GetFeature(application.Context).Options);
        }
    }

    private static OpenApiDocumentFeature GetFeature(IWebApplicationContext context)
        => context.Features.OfType<OpenApiDocumentFeature>().LastOrDefault()
            ?? throw new InvalidOperationException(
                "OpenAPI has not been registered. Call builder.Services.AddOpenApi() before mapping or reading the document.");

    private static OpenApiFormat GetFormat(string pattern)
        => pattern.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || pattern.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            ? OpenApiFormat.Yaml
            : OpenApiFormat.Json;
}
