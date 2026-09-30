using System;
using System.Linq;

using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Internal;

/// <summary>
/// Shared plumbing for the endpoint-mapping verbs on web applications and route groups.
/// </summary>
internal static class EndpointMapping
{
    /// <summary>
    /// Resolves the application's per-application router builder, which <c>AddRouting</c> registers.
    /// </summary>
    /// <param name="context">The web application context.</param>
    /// <returns>The application's router builder.</returns>
    /// <exception cref="InvalidOperationException">Routing has not been registered on the application.</exception>
    public static IRouterBuilder GetRouterBuilder(IWebApplicationContext context)
    {
        IRouterFeature? feature = context.Features.OfType<IRouterFeature>().FirstOrDefault();

        return feature?.Builder
            ?? throw new InvalidOperationException("No router builder was registered. Call AddRouting() on the application builder before mapping endpoints.");
    }

    /// <summary>
    /// Produces the exception thrown by the typed <see cref="Delegate"/> overloads when the source
    /// generator did not intercept the call site.
    /// </summary>
    /// <returns>The exception to throw.</returns>
    public static NotSupportedException RequiresSourceGenerator()
        => new(
            "This typed endpoint overload is a placeholder that the Cohesion Web source generator " +
            "(Assimalign.Cohesion.SourceGeneration.Web) rewrites at the call site. Reaching it at run time means the " +
            "generator did not intercept the call: reference the generator with " +
            "<CohesionAnalyzerReference Include=\"Assimalign.Cohesion.SourceGeneration.Web\" /> and allow-list its " +
            "generated namespace with <InterceptorsNamespaces>$(InterceptorsNamespaces);Assimalign.Cohesion.Web.Api.Generated</InterceptorsNamespaces>.");
}
