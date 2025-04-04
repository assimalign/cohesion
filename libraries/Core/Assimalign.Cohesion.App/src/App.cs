using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.App;

using Assimalign.Cohesion.Hosting;

public abstract class App : IHostLifecycleService
{
    protected virtual Task StartingAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
    protected abstract Task StartAsync(CancellationToken cancellationToken);
    protected virtual Task StartedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
    protected virtual Task StoppedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
    protected abstract Task StopAsync(CancellationToken cancellationToken);

    protected virtual Task StoppingAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    Task IHostService.StartAsync(CancellationToken cancellationToken)
    {
        return StartAsync(cancellationToken);
    }

    Task IHostLifecycleService.StartedAsync(CancellationToken cancellationToken)
    {
        return StartedAsync(cancellationToken);
    }

    Task IHostLifecycleService.StartingAsync(CancellationToken cancellationToken)
    {
        return StartingAsync(cancellationToken);
    }

    Task IHostService.StopAsync(CancellationToken cancellationToken)
    {
        return StopAsync(cancellationToken);
    }

    Task IHostLifecycleService.StoppedAsync(CancellationToken cancellationToken)
    {
        return StoppedAsync(cancellationToken);
    }

    Task IHostLifecycleService.StoppingAsync(CancellationToken cancellationToken)
    {
        return StoppingAsync(cancellationToken);
    }
}