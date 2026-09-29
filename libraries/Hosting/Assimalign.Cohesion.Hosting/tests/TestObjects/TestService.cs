using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Tests;


internal class TestService : IHostService
{
    private readonly Func<CancellationToken, Task> _factory;

    private Task? _task;
    private CancellationTokenSource? _cancellationTokenSource;


    public TestService(Func<CancellationToken, Task> factory)
    {
        this._factory = factory;
    }

    public ServiceId Id => throw new NotImplementedException();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _task = _factory(_cancellationTokenSource.Token);

        if (_task.IsCompleted)
        {
            return _task;
        }

        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
