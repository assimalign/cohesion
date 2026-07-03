using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Tests;

internal sealed class DelegateHostService : IHostService
{
    private readonly Func<CancellationToken, Task> _start;
    private readonly Func<CancellationToken, Task>? _stop;

    public DelegateHostService(
        Func<CancellationToken, Task> start,
        Func<CancellationToken, Task>? stop = null)
    {
        _start = start;
        _stop = stop;
    }

    public ServiceId Id { get; } = ServiceId.New();

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return _start(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return _stop?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }
}
