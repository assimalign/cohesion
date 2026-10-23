using System;
using System.Threading;

namespace Assimalign.Cohesion.MessageHub.Hosting.Internal;

using Assimalign.Cohesion.Hosting;

// Dedicated OS thread per the execution menu: a synchronous blocking loop must own its
// thread for its entire life instead of occupying the pool. See docs/DESIGN.md.
[global::System.Diagnostics.DebuggerDisplay("JournalFlushService {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class JournalFlushService : DedicatedThreadService
{
    protected override void Run(CancellationToken cancellationToken)
    {
        // TODO: flush the message journal with synchronous file I/O. The placeholder blocks until the host stops so the scaffolded
        // application starts and drains cleanly.
        cancellationToken.WaitHandle.WaitOne();
    }

    private sealed class DebugView
    {
        private readonly JournalFlushService _target;

        public DebugView(JournalFlushService value)
        {
            _target = value;
        }

        public ServiceId Id => _target.Id;
    }
}