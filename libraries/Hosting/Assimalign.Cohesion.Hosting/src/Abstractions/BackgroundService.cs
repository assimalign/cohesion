using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// A base class for a long-running, pool-scheduled unit of asynchronous work hosted within
/// an <see cref="IHost"/>. <see cref="ExecuteAsync"/> runs synchronously on the starting
/// thread until its first await and cooperates with the thread pool thereafter.
/// </summary>
/// <remarks>
/// Use this base for asynchronous I/O loops such as accepting connections, timer ticks, or
/// queue polling. A synchronous blocking loop that must own an OS thread for its entire
/// life should use <see cref="DedicatedThreadService"/> instead of blocking a thread-pool
/// thread here.
/// </remarks>
public abstract class BackgroundService : IHostService, IDisposable
{
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _task;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundService"/> class.
    /// </summary>
    protected BackgroundService()
    {
        Id = ServiceId.New();
    }

    /// <summary>
    /// A unique identifier for the host service.
    /// </summary>
    public virtual ServiceId Id { get; }

    /// <summary>
    /// The long-running work of the service. The returned task represents the entire
    /// lifetime of the work: it must complete only when the work has finished or has
    /// observed cancellation.
    /// </summary>
    /// <param name="cancellationToken">Signaled when <see cref="StopAsync"/> is called.</param>
    /// <returns>A task that represents the long-running work.</returns>
    /// <remarks>
    /// The body is pool-scheduled: it runs synchronously on the starting thread until its
    /// first await, and its continuations run on thread-pool threads thereafter. Do not
    /// block in the body (no <see cref="Thread.Sleep(int)"/>, no synchronous I/O waits) -
    /// blocking work starves the pool and belongs on <see cref="DedicatedThreadService"/>.
    /// </remarks>
    protected abstract Task ExecuteAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Starts the service by launching <see cref="ExecuteAsync"/> and storing the returned
    /// work task so <see cref="StopAsync"/> can join it on shutdown.
    /// </summary>
    /// <param name="cancellationToken">Cancels the executing work if signaled.</param>
    /// <returns>
    /// The work task itself when it completed synchronously, so a synchronous failure
    /// surfaces to the host during start; otherwise a completed task.
    /// </returns>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Launch and store the REAL work task. No Task.Factory.StartNew wrapper: the task
        // stored here is the one StopAsync must join and the one that carries faults.
        try
        {
            _task = ExecuteAsync(_cancellationTokenSource.Token);
        }
        catch
        {
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
            throw;
        }

        if (_task.IsCompleted)
        {
            return _task;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Signals cancellation to the executing work and waits for it to drain within the
    /// caller's shutdown budget.
    /// </summary>
    /// <param name="cancellationToken">Bounds the wait for the work to drain.</param>
    /// <returns>A task that completes once the work has finished.</returns>
    /// <exception cref="OperationCanceledException">
    /// The work did not finish before <paramref name="cancellationToken"/> was signaled.
    /// A cooperative exit of the work in response to the stop signal is a clean stop and
    /// does not throw; any other exception thrown by <see cref="ExecuteAsync"/> is
    /// rethrown here so the host observes it.
    /// </exception>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? task = _task;

        // Stop called without start.
        if (task is null)
        {
            return;
        }

        _cancellationTokenSource!.Cancel();

        try
        {
            // Join the real work task; a fault thrown by ExecuteAsync surfaces here.
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (task.IsCanceled)
        {
            // The work exited cooperatively in response to the stop signal: a clean stop.
        }
        finally
        {
            // Only clear run-state once the work actually finished; on a drain timeout the
            // task is still running and a retried StopAsync must be able to rejoin it.
            if (task.IsCompleted)
            {
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;
                _task = null;
            }
        }
    }

    /// <summary>
    /// Signals cancellation to the executing work without waiting for it to finish.
    /// </summary>
    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
    }
}
