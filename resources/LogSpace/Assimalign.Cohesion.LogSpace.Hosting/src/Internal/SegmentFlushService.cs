using System;
using System.Threading;

namespace Assimalign.Cohesion.LogSpace.Hosting.Internal;

using Assimalign.Cohesion.Hosting;

// Dedicated OS thread per the execution menu: a synchronous blocking loop must own its
// thread for its entire life instead of occupying the pool. See docs/DESIGN.md.
internal sealed class SegmentFlushService : DedicatedThreadService
{
    protected override void Run(CancellationToken cancellationToken)
    {
        // TODO: flush log segments with synchronous file I/O. The placeholder blocks until the host stops so the scaffolded
        // application starts and drains cleanly.
        cancellationToken.WaitHandle.WaitOne();
    }
}