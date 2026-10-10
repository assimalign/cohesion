using System.ComponentModel;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Internal;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// The factory behind <c>builder.Services.AddRouting()</c>. Applications call that verb, not this type.
/// </summary>
/// <remarks>
/// <para>
/// The verb is a component integration (<c>Properties/ComponentIntegrations.cs</c>): every compilation
/// that references both this package and <c>Assimalign.Cohesion.DependencyInjection</c>, as every Web
/// application does through <c>Web.Hosting</c>, receives <c>AddRouting()</c> on
/// <c>IServiceProviderBuilder</c>. The verb registers the feature <see cref="CreateFeature"/> returns as an
/// <see cref="IHttpFeature"/> singleton, so this package takes no dependency-injection reference and the
/// hosting runtime takes no reference to this package for registration (owner decision 34, #1380).
/// </para>
/// <para>
/// The type is public only because the generated verb, compiled into the application, calls it. It is the
/// static-factory shape because <c>AddRouting</c> takes no configuration: the builder-template shape would
/// make every caller pass an empty callback.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class RoutingComponents
{
    /// <summary>
    /// Creates the application's <see cref="IRouterFeature"/>: the per-application router builder that
    /// <c>UseRouting</c> resolves and the <c>Map</c> verbs map into.
    /// </summary>
    /// <returns>A new router feature, registered by the verb as an <see cref="IHttpFeature"/> singleton.</returns>
    public static IHttpFeature CreateFeature() => new RouterFeature();
}
