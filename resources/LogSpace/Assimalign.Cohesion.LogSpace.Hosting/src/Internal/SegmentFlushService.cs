using System.Threading;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.LogSpace.Hosting.Internal;

// Dedicated OS thread per the execution menu: a synchronous blocking loop must own its
// thread for its entire life instead of occupying the pool. See docs/DESIGN.md.
internal sealed class SegmentFlushService : DedicatedThreadService
{
    private readonly LogSegmentStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentFlushService"/> class.
    /// </summary>
    /// <param name="store">The segment store this service flushes on its dedicated thread.</param>
    public SegmentFlushService(LogSegmentStore store)
    {
        _store = store;
    }

    protected override void Run(CancellationToken cancellationToken)
    {
        while (!cancellationToken.WaitHandle.WaitOne(250))
        {
            _store.Flush();
        }
        _store.Flush(stopping: true);
    }
}
