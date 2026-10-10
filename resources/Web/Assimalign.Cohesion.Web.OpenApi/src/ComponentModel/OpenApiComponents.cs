using System;
using System.ComponentModel;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.OpenApi.Internal;

namespace Assimalign.Cohesion.Web.OpenApi;

/// <summary>
/// The factory behind <c>builder.Services.AddOpenApi(...)</c>. Applications call that verb, not this
/// type.
/// </summary>
/// <remarks>
/// <para>
/// The verb is a component integration (<c>Properties/ComponentIntegrations.cs</c>): every compilation
/// that references both this package and <c>Assimalign.Cohesion.DependencyInjection</c>, as every Web
/// application does through <c>Web.Hosting</c>, receives <c>AddOpenApi(configure)</c> on
/// <c>IServiceProviderBuilder</c>. The verb registers the feature <see cref="CreateFeature"/> returns as
/// an <see cref="IHttpFeature"/> singleton (owner decision 34, #1380).
/// </para>
/// <para>
/// The type is public only because the generated verb, compiled into the application, calls it. It is
/// the static-factory shape because the verb's configuration is optional: the builder-template shape
/// would make every caller that keeps the default document pass an empty callback.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class OpenApiComponents
{
    /// <summary>
    /// Creates the application's OpenAPI document feature, which carries the document options to
    /// <c>MapOpenApi</c> and <c>GetOpenApiDescriptionProvider</c>.
    /// </summary>
    /// <remarks>
    /// The options become read-only when <paramref name="configure"/> returns. Registering again replaces
    /// the earlier registration.
    /// </remarks>
    /// <param name="configure">An optional callback that configures the document.</param>
    /// <returns>The document feature, registered by the verb as an <see cref="IHttpFeature"/> singleton.</returns>
    public static IHttpFeature CreateFeature(Action<OpenApiOptions>? configure = null)
    {
        OpenApiOptions options = new();
        configure?.Invoke(options);
        options.MakeReadOnly();

        return new OpenApiDocumentFeature(options);
    }
}
