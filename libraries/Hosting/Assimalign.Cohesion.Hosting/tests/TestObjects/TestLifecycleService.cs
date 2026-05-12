using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Tests;

public class TestLifecycleService : IHostLifecycleService
{
    private readonly Action<Lifecycle> _action;

    public TestLifecycleService(Action<Lifecycle> action)
    {
        _action = action;
    }

    public enum Lifecycle 
    {
        Starting,
        Start,
        Started,
        Stopping,
        Stop,
        Stopped
    }

    public ServiceId Id { get; } = ServiceId.New();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _action.Invoke(Lifecycle.Start);
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        _action.Invoke(Lifecycle.Started);
        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        _action.Invoke(Lifecycle.Starting);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _action.Invoke(Lifecycle.Stop);
        return Task.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        _action.Invoke(Lifecycle.Stopped);
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        _action.Invoke(Lifecycle.Stopping);
        return Task.CompletedTask;
    }
}
