using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

public abstract class BackgroundService : IHostService, IDisposable
{
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _task;

    protected BackgroundService()
    {
        Id = ServiceId.New();
    }

    public virtual ServiceId Id { get; }

    protected abstract Task ExecuteAsync(CancellationToken cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Create linked token to allow cancelling executing task from provided token
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Execute all of ExecuteAsync asynchronously, and store the task we're executing so that we can wait for it later.
        _task = Task.Factory.StartNew(
            async () => await ExecuteAsync(_cancellationTokenSource.Token),
            TaskCreationOptions.LongRunning);

        // Always return a completed task.  Any result from ExecuteAsync will be handled by the Host.
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Stop called without start
        if (_task is null)
        {
            return;
        }

        try
        {
            // Signal cancellation to the executing method
            _cancellationTokenSource!.Cancel();
        }
        finally
        {
            await _task.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
    }
}
