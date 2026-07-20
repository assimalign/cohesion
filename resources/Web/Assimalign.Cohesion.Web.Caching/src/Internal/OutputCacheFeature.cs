using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Caching.Internal;

/// <summary>
/// The <see cref="IOutputCacheFeature"/> the middleware installs on every exchange: a thin, stateless
/// handle that forwards tag eviction to the middleware's backing <see cref="IOutputCacheStore"/>. One
/// instance is captured at builder time and installed on each request — it holds no per-request state.
/// </summary>
internal sealed class OutputCacheFeature : IOutputCacheFeature
{
    private readonly IOutputCacheStore _store;

    public OutputCacheFeature(IOutputCacheStore store)
    {
        _store = store;
    }

    public string Name => nameof(IOutputCacheFeature);

    public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        return _store.EvictByTagAsync(tag, cancellationToken);
    }
}
