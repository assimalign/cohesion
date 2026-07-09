namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// The default, shareable <see cref="IApiEndpointMetadata"/> instance. Attach
/// <see cref="Instance"/> to an endpoint's metadata to opt it into non-redirecting
/// challenge/forbid behavior (bare <c>401</c>/<c>403</c> instead of a login/access-denied
/// redirect).
/// </summary>
public sealed class ApiEndpointMetadata : IApiEndpointMetadata
{
    private ApiEndpointMetadata()
    {
    }

    /// <summary>
    /// Gets the shared marker instance. It is immutable and carries no state, so a single
    /// instance is safe to attach to any number of endpoints.
    /// </summary>
    public static ApiEndpointMetadata Instance { get; } = new();
}
