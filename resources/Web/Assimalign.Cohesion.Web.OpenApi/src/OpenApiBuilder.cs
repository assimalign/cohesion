using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.OpenApi.Internal;

namespace Assimalign.Cohesion.Web.OpenApi;

/// <summary>
/// Composes the application's OpenAPI document registration: the <see cref="OpenApiOptions"/> that
/// <c>MapOpenApi</c> and <c>GetOpenApiDescriptionProvider</c> read.
/// </summary>
/// <remarks>
/// <para>
/// Applications receive one in the <c>builder.Services.AddOpenApi(openApi => ...)</c> callback and configure
/// <see cref="Options"/>, for example <c>openApi => openApi.Options.Title = "Orders API"</c>. That verb is a
/// component integration (<c>Properties/ComponentIntegrations.cs</c>, owner decisions 34 and 39): it creates
/// the builder, runs the optional callback, calls <see cref="Build"/>, and registers the result as an
/// <see cref="IHttpFeature"/> singleton. Called bare, <c>AddOpenApi()</c> registers the default document:
/// the running application's name as the title, API version <c>1.0.0</c>, and OpenAPI 3.1. A composition
/// surface without a service container registers <see cref="Build"/>'s result through
/// <c>IWebApplicationBuilder.AddFeature</c>.
/// </para>
/// </remarks>
public sealed class OpenApiBuilder
{
    private readonly OpenApiOptions _options = new();

    /// <summary>
    /// Initializes a builder with the default document options.
    /// </summary>
    public OpenApiBuilder()
    {
    }

    /// <summary>
    /// Gets the document options being composed.
    /// </summary>
    /// <remarks>
    /// The options become read-only when <see cref="Build"/> runs; every mutator then throws
    /// <see cref="System.InvalidOperationException"/>, so the document a request receives cannot change
    /// underneath it.
    /// </remarks>
    public OpenApiOptions Options => _options;

    /// <summary>
    /// Makes <see cref="Options"/> read-only and builds the document feature that carries them to
    /// <c>MapOpenApi</c> and <c>GetOpenApiDescriptionProvider</c>.
    /// </summary>
    /// <remarks>
    /// Registering the feature again replaces the earlier registration: an exchange carries one.
    /// </remarks>
    /// <returns>The document feature, an <see cref="IHttpFeature"/> for the application to register.</returns>
    public IHttpFeature Build()
    {
        _options.MakeReadOnly();

        return new OpenApiDocumentFeature(_options);
    }
}
