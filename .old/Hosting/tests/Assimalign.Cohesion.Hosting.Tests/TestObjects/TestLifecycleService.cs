using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Tests;

public class TestLifecycleService : IHostLifecycleService
{
    private readonly Action<Lifecycle> action;

    public TestLifecycleService(Action<Lifecycle> action)
    {
        this.action = action;
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


    public Task StartAsync(CancellationToken cancellationToken)
    {
        action.Invoke(Lifecycle.Start);
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        action.Invoke(Lifecycle.Started);
        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        action.Invoke(Lifecycle.Starting);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        action.Invoke(Lifecycle.Stop);
        return Task.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        action.Invoke(Lifecycle.Stopped);
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        action.Invoke(Lifecycle.Stopping);
        return Task.CompletedTask;
    }
}
