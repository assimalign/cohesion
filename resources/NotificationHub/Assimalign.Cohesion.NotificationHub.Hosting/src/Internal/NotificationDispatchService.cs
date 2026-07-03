using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.NotificationHub.Hosting.Internal;

using Assimalign.Cohesion.Hosting;

// Pool-scheduled per the execution menu: an asynchronous I/O loop cooperates with the
// thread pool via await instead of owning a thread. See docs/DESIGN.md.
internal sealed class NotificationDispatchService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // TODO: fan notifications out to subscribers and delivery channels. The placeholder parks until the host stops so the scaffolded
        // application starts and drains cleanly.
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }
}