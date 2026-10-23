using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.IoTHub.Hosting.Internal;

using Assimalign.Cohesion.Hosting;

// Pool-scheduled per the execution menu: an asynchronous I/O loop cooperates with the
// thread pool via await instead of owning a thread. See docs/DESIGN.md.
[global::System.Diagnostics.DebuggerDisplay("DeviceIngressService {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class DeviceIngressService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // TODO: accept device connections and telemetry. The placeholder parks until the host stops so the scaffolded
        // application starts and drains cleanly.
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    private sealed class DebugView
    {
        private readonly DeviceIngressService _target;

        public DebugView(DeviceIngressService value)
        {
            _target = value;
        }

        public ServiceId Id => _target.Id;
    }
}