namespace Assimalign.Cohesion.Caching;

/// <summary>
/// Marker contract for in-process Cohesion caches.
/// </summary>
/// <remarks>
/// Implementations are expected to store every entry in the host process. Eviction, expiration,
/// and size limits are honored locally; there is no cross-process coordination.
/// </remarks>
public interface IMemoryCache : ICache
{
}
