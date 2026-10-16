using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Media.Tests;

// An empty single-pass source synthesized in code. Optional lookups deliberately use the base defaults.
internal sealed class StreamingMediaSampleIndex : MediaSampleIndex
{
    public override ulong TrackId => 1;
    public override long? Count => null;

    protected override async IAsyncEnumerable<MediaSample> ReadSamplesCoreAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ValueTask.CompletedTask;
        yield break;
    }
}
