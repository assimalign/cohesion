using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

public abstract class BackgroundService : IHostLifecycleService
{

    protected BackgroundService()
    {
        
    }


    public virtual Task StartingAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
    public virtual Task StartAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
    public virtual Task StartedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }


    public abstract Task ExecuteAsync(CancellationToken cancellationToken);

    

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
    public Task StopAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
