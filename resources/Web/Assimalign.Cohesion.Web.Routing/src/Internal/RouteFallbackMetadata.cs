namespace Assimalign.Cohesion.Web.Routing.Internal;

/// <summary>
/// Marks a fallback route (<c>MapFallback</c>). The router evaluates fallback routes after every other
/// route, whatever their precedence, and a fallback route never contributes to a 405: a request whose
/// path only a fallback matched, with a method the fallback does not accept, is a 404, not a 405.
/// </summary>
internal sealed class RouteFallbackMetadata
{
    /// <summary>
    /// Gets the shared marker instance.
    /// </summary>
    public static RouteFallbackMetadata Instance { get; } = new();

    private RouteFallbackMetadata()
    {
    }
}
