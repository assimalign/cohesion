namespace Assimalign.Cohesion.Web;

/// <summary>
/// Endpoint metadata that leaves an endpoint out of the application's API description. The convention
/// verb <c>ExcludeFromDescription</c> attaches it to a route or to every route of a group.
/// </summary>
/// <remarks>
/// <para>
/// The marker is absolute: an endpoint that carries it anywhere, on the route or on any group above it,
/// is not described. It changes nothing about how the endpoint is matched or run.
/// </para>
/// <para>
/// The marker carries no data, so the shared <see cref="Instance"/> serves every endpoint. This sealed
/// carrier is the metadata contract; there is deliberately no interface.
/// </para>
/// </remarks>
public sealed class ExcludeFromDescriptionMetadata
{
    private ExcludeFromDescriptionMetadata()
    {
    }

    /// <summary>
    /// Gets the shared marker instance.
    /// </summary>
    public static ExcludeFromDescriptionMetadata Instance { get; } = new();
}
