using System;
using System.ComponentModel;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authorization.Internal;

namespace Assimalign.Cohesion.Web.Authorization;

/// <summary>
/// The factory behind <c>builder.Services.AddAuthorization(...)</c>. Applications call that verb, not
/// this type.
/// </summary>
/// <remarks>
/// <para>
/// The verb is a component integration (<c>Properties/ComponentIntegrations.cs</c>): every compilation
/// that references both this package and <c>Assimalign.Cohesion.DependencyInjection</c>, as every Web
/// application does through <c>Web.Hosting</c>, receives <c>AddAuthorization(configure)</c> on
/// <c>IServiceProviderBuilder</c>. The verb registers the feature <see cref="CreateFeature"/> returns as
/// an <see cref="IHttpFeature"/> singleton (owner decision 34, #1380).
/// </para>
/// <para>
/// The type is public only because the generated verb, compiled into the application, calls it. It is
/// the static-factory shape because the verb's configuration is optional: the builder-template shape
/// would make every caller that registers only the defaults pass an empty callback.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class AuthorizationComponents
{
    /// <summary>
    /// Creates the application's authorization feature: the default policy, the optional fallback
    /// policy, and the named policies endpoints reference, which <c>UseAuthorization</c> evaluates.
    /// </summary>
    /// <remarks>
    /// The options become read-only when <paramref name="configure"/> returns. Registering again replaces
    /// the earlier registration.
    /// </remarks>
    /// <param name="configure">An optional callback that configures the policies.</param>
    /// <returns>The authorization feature, registered by the verb as an <see cref="IHttpFeature"/> singleton.</returns>
    public static IHttpFeature CreateFeature(Action<AuthorizationOptions>? configure = null)
    {
        AuthorizationOptions options = new();
        configure?.Invoke(options);
        options.MakeReadOnly();

        return new AuthorizationFeature(options);
    }
}
