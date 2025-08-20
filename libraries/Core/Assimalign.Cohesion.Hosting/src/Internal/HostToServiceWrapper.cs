using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Internal;

internal class HostToServiceWrapper : IHostService
{
    private readonly IHost _host;

    public HostToServiceWrapper(IHost host)
    {
        _host = host;
    }
    public ServiceId Id => (Ulid)_host.Id;
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return _host.StartAsync(cancellationToken);
    }
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return _host.StopAsync(cancellationToken);
    }
}
