using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

internal sealed class DelegateHostService : IHostService
{
    private readonly Func<CancellationToken, Task> _start;
    private readonly Func<CancellationToken, Task> _stop;

    public DelegateHostService(Func<CancellationToken, Task> start, Func<CancellationToken, Task> stop)
    {
        _start = start;
        _stop = stop;
    }

    public ServiceId Id { get; } = ServiceId.New();
    public Task StartAsync(CancellationToken cancellationToken = default) => _start(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken = default) => _stop(cancellationToken);
}
