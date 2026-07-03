using System;
using System.Threading;

namespace Assimalign.Cohesion.MediaHub.Hosting.Internal;

using Assimalign.Cohesion.Hosting;

// Dedicated OS thread per the execution menu: a synchronous blocking loop must own its
// thread for its entire life instead of occupying the pool. See docs/DESIGN.md.
internal sealed class ContentIoService : DedicatedThreadService
{
    protected override void Run(CancellationToken cancellationToken)
    {
        // TODO: read and write media content with blocking file I/O. The placeholder blocks until the host stops so the scaffolded
        // application starts and drains cleanly.
        cancellationToken.WaitHandle.WaitOne();
    }
}