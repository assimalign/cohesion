using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Internal;

internal sealed class HostToServiceWrapper : BackgroundService
{
    private readonly IHost _host;

    public HostToServiceWrapper(IHost host)
    {
        _host = host;
    }

    public override ServiceId Id => (Ulid)_host.Id;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await _host.StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_host.Context.State.Equals(HostState.Running))
                {
                    break;
                }
            }

            await _host.StopAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _host.StopAsync().ConfigureAwait(false);
        }
    }
}
