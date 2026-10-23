using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authorization.Internal;

namespace Assimalign.Cohesion.Web.Authorization;

/// <summary>
/// Composes authorization for a web application: the <see cref="AuthorizationOptions"/> (the default
/// policy, the optional fallback policy, and the named policies) that <c>UseAuthorization</c> evaluates.
/// </summary>
/// <remarks>
/// <para>
/// Applications receive one in the <c>builder.Services.AddAuthorization(authorization => ...)</c>
/// callback and configure <see cref="Options"/>, for example
/// <c>authorization.Options.AddPolicy("admins", policy => policy.RequireRole("admin"))</c>. That verb is a
/// component integration (<c>Properties/ComponentIntegrations.cs</c>, owner decisions 34 and 39): it
/// creates the builder, runs the optional callback, calls <see cref="Build"/>, and registers the result
/// as an <see cref="IHttpFeature"/> singleton. Called bare, <c>AddAuthorization()</c> registers the
/// defaults: an authenticated user for <c>RequireAuthorization()</c> and no fallback policy. A composition
/// surface without a service container registers <see cref="Build"/>'s result through
/// <c>IWebApplicationBuilder.AddFeature</c>.
/// </para>
/// <para>
/// Composition is dependency-free: policies are values captured at builder time, with no service
/// container, configuration binding, or request-time service location.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("AuthorizationBuilder")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class AuthorizationBuilder
{
    private readonly AuthorizationOptions _options = new();

    /// <summary>
    /// Initializes a builder with the default options: a default policy that requires an authenticated
    /// user, no fallback policy, and no named policies.
    /// </summary>
    public AuthorizationBuilder()
    {
    }

    /// <summary>
    /// Gets the authorization options being composed.
    /// </summary>
    /// <remarks>
    /// The options become read-only when <see cref="Build"/> runs; every mutator then throws
    /// <see cref="System.InvalidOperationException"/>, so the policies <c>UseAuthorization</c> evaluates
    /// cannot change underneath concurrent requests.
    /// </remarks>
    public AuthorizationOptions Options => _options;

    /// <summary>
    /// Makes <see cref="Options"/> read-only and builds the authorization feature that carries them to
    /// <c>UseAuthorization</c> and <c>TryGetAuthorizationOptions</c>.
    /// </summary>
    /// <remarks>
    /// Registering the feature again replaces the earlier registration: an exchange carries one.
    /// </remarks>
    /// <returns>The authorization feature, an <see cref="IHttpFeature"/> for the application to register.</returns>
    public IHttpFeature Build()
    {
        _options.MakeReadOnly();

        return new AuthorizationFeature(_options);
    }

    private sealed class DebugView
    {
        private readonly AuthorizationBuilder _target;

        public DebugView(AuthorizationBuilder value)
        {
            _target = value;
        }

        public AuthorizationOptions Options => _target.Options;
    }
}
