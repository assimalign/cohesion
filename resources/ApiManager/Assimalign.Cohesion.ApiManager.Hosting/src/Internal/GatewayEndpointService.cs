using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApiManager.Hosting.Internal;

using Assimalign.Cohesion.Hosting;

// Pool-scheduled per the execution menu: an asynchronous I/O loop cooperates with the
// thread pool via await instead of owning a thread. See docs/DESIGN.md.
[global::System.Diagnostics.DebuggerDisplay("GatewayEndpointService {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class GatewayEndpointService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // TODO: accept API traffic and proxy it to backend services. The placeholder parks until the host stops so the scaffolded
        // application starts and drains cleanly.
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    private sealed class DebugView
    {
        private readonly GatewayEndpointService _target;

        public DebugView(GatewayEndpointService value)
        {
            _target = value;
        }

        public ServiceId Id => _target.Id;
    }
}