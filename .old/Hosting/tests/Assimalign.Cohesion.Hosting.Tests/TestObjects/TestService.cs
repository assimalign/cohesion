using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Tests;


internal class TestService : IHostService
{
    private readonly Func<CancellationToken, Task> factory;

    private Task? task;
    private CancellationTokenSource? cancellationTokenSource;


    public TestService(Func<CancellationToken, Task> factory)
    {
        this.factory = factory;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        task = factory(cancellationTokenSource.Token);

        if (task.IsCompleted)
        {
            return task;
        }

        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
