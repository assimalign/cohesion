using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.MessageHub.Hosting.Internal;

using Assimalign.Cohesion.Hosting;

// Pool-scheduled per the execution menu: an asynchronous I/O loop cooperates with the
// thread pool via await instead of owning a thread. See docs/DESIGN.md.
[global::System.Diagnostics.DebuggerDisplay("BrokerEndpointService {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class BrokerEndpointService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // TODO: accept broker connections and dispatch messages. The placeholder parks until the host stops so the scaffolded
        // application starts and drains cleanly.
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    private sealed class DebugView
    {
        private readonly BrokerEndpointService _target;

        public DebugView(BrokerEndpointService value)
        {
            _target = value;
        }

        public ServiceId Id => _target.Id;
    }
}