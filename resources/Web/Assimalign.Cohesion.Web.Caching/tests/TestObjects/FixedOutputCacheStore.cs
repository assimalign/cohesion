using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Caching.Tests.TestObjects;

internal sealed class FixedOutputCacheStore : IOutputCacheStore
{
    private readonly OutputCacheEntry _entry;

    public FixedOutputCacheStore(OutputCacheEntry entry) => _entry = entry;

    public ValueTask<OutputCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<OutputCacheEntry?>(_entry);

    public ValueTask SetAsync(string key, OutputCacheEntry entry, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
