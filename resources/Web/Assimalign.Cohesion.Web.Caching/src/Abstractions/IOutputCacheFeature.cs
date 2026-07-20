using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Caching;

/// <summary>
/// The per-exchange feature the output-cache middleware installs so application code — a handler, a
/// later middleware — can purge cached responses by tag without capturing the backing
/// <see cref="IOutputCacheStore"/> itself. Resolve it with
/// <c>context.Features.Get&lt;IOutputCacheFeature&gt;()</c>.
/// </summary>
/// <remarks>
/// The feature is a thin, stateless handle over the middleware's store: it exposes only tag eviction,
/// the invalidation primitive an application drives when the underlying data changes (for example after
/// a write that supersedes a cached representation).
/// </remarks>
public interface IOutputCacheFeature : IHttpFeature
{
    /// <summary>
    /// Evicts every cached response tagged with <paramref name="tag"/>.
    /// </summary>
    /// <param name="tag">The tag whose entries are removed.</param>
    /// <param name="cancellationToken">A token to cancel the eviction.</param>
    /// <returns>A task that completes when the tagged entries have been removed.</returns>
    /// <exception cref="System.ArgumentException"><paramref name="tag"/> is <see langword="null"/> or empty.</exception>
    ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken = default);
}
