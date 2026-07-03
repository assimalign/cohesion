using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

using Assimalign.Cohesion.Hosting;

// Dedicated OS thread per the execution menu: a synchronous blocking loop must own its
// thread for its entire life instead of occupying the pool. See docs/DESIGN.md.
internal sealed class WriteAheadFlushService : DedicatedThreadService
{
    protected override void Run(CancellationToken cancellationToken)
    {
        // TODO: flush the write-ahead log with synchronous file I/O. The placeholder blocks until the host stops so the scaffolded
        // application starts and drains cleanly.
        cancellationToken.WaitHandle.WaitOne();
    }
}