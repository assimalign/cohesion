using System.Threading;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.LogSpace.Hosting;

// Dedicated OS thread per the execution menu: a synchronous blocking loop must own its
// thread for its entire life instead of occupying the pool. See docs/DESIGN.md.
internal sealed class SegmentFlushService(LogSegmentStore store) : DedicatedThreadService
{
    protected override void Run(CancellationToken cancellationToken)
    {
        while (!cancellationToken.WaitHandle.WaitOne(250))
        {
            store.Flush();
        }
        store.Flush(stopping: true);
    }
}
