using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Authorization.Internal;

/// <summary>
/// Carries the read-only <see cref="AuthorizationOptions"/> from <c>AddAuthorization</c> (builder
/// time) to <c>UseAuthorization</c> (pipeline composition). The application's registered features are
/// the only channel between the two verbs, so the options travel as a feature; the middleware resolves
/// it once, when the pipeline is composed. The feature type stays internal: readers go through the
/// public <c>TryGetAuthorizationOptions</c> accessor on the application context, which hands out the
/// read-only options and nothing that could register a second channel.
/// </summary>
internal sealed class AuthorizationFeature : IHttpFeature
{
    public AuthorizationFeature(AuthorizationOptions options)
    {
        Options = options;
    }

    /// <inheritdoc />
    public string Name => nameof(AuthorizationFeature);

    /// <summary>
    /// Gets the read-only options.
    /// </summary>
    public AuthorizationOptions Options { get; }
}
