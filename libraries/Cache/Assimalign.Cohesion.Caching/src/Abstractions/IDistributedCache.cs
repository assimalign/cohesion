namespace Assimalign.Cohesion.Caching;

/// <summary>
/// Marker contract for out-of-process (distributed) Cohesion caches.
/// </summary>
/// <remarks>
/// Distributed caches share state across processes or hosts. They typically layer their own
/// asynchronous APIs on top of the synchronous <see cref="ICache"/> surface; the foundation
/// package does not prescribe a specific async shape.
/// </remarks>
public interface IDistributedCache : ICache
{
}
