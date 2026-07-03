using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Internal;

internal sealed class HostToServiceWrapper : BackgroundService
{
    private readonly IHost _host;
    private readonly HostContext _context;

    public HostToServiceWrapper(IHost host, HostContext context)
    {
        _host = host;
        _context = context;
    }

    public override ServiceId Id => (Ulid)_host.Id;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await _host.StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Park - without polling - until the wrapped host transitions to Stopped on
            // its own or the outer host signals this service to stop.
            await _context.WhenStoppedAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The outer host is stopping this service; fall through to stop the wrapped host.
        }

        // Stop with a fresh token so the wrapped host gets its own shutdown budget: on the
        // outer stop path the wrapper's token is already cancelled. A host that stopped on
        // its own makes this a no-op.
        await _host.StopAsync().ConfigureAwait(false);
    }
}
