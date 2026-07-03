using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// A base class for a synchronous, blocking unit of work hosted within an <see cref="IHost"/>
/// that owns a dedicated background OS thread for its entire life.
/// </summary>
/// <remarks>
/// Use this base for blocking loops - synchronous file or device I/O, flush workers, or
/// tight CPU loops - that must not occupy a thread-pool thread. For asynchronous I/O loops
/// use <see cref="BackgroundService"/> instead; a component that owns several threads
/// internally should implement <see cref="IHostService"/> directly.
/// </remarks>
public abstract class DedicatedThreadService : IHostService, IDisposable
{
    private CancellationTokenSource? _cancellationTokenSource;
    private TaskCompletionSource? _exited;

    /// <summary>
    /// Initializes a new instance of the <see cref="DedicatedThreadService"/> class.
    /// </summary>
    protected DedicatedThreadService()
    {
        Id = ServiceId.New();
    }

    /// <summary>
    /// A unique identifier for the host service.
    /// </summary>
    public virtual ServiceId Id { get; }

    /// <summary>
    /// The blocking work of the service. Runs on a dedicated background thread for its
    /// entire life and should return - or throw <see cref="OperationCanceledException"/> -
    /// once <paramref name="cancellationToken"/> is signaled.
    /// </summary>
    /// <param name="cancellationToken">Signaled when <see cref="StopAsync"/> is called.</param>
    /// <remarks>
    /// This member is deliberately synchronous. A dedicated thread executes exactly one
    /// call frame, so the work finishes precisely when this method returns and the
    /// thread's life maps one-to-one onto the work's life. A <see cref="Task"/>-returning
    /// member here would be an illusion: an async body leaves its starting thread at its
    /// first await and its continuations migrate to the thread pool - the same false
    /// promise <see cref="TaskCreationOptions.LongRunning"/> makes for async work.
    /// Blocking calls (<see cref="WaitHandle.WaitOne()"/>, synchronous I/O, sleep-paced
    /// loops) are the intended vocabulary in this body. Work that needs <c>await</c>
    /// belongs on <see cref="BackgroundService"/> instead; a service that needs both
    /// shapes should be split into one service of each base, or implement
    /// <see cref="IHostService"/> directly and own its threads.
    /// </remarks>
    protected abstract void Run(CancellationToken cancellationToken);

    /// <summary>
    /// Starts the service by launching <see cref="Run"/> on a new dedicated background
    /// thread named after the service type.
    /// </summary>
    /// <param name="cancellationToken">Cancels the executing work if signaled.</param>
    /// <returns>A completed task once the thread has been started.</returns>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = cancellationTokenSource.Token;

        var thread = new Thread(() =>
        {
            try
            {
                Run(token);
                exited.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                // The work exited cooperatively in response to the stop signal: a clean stop.
                exited.TrySetResult();
            }
            catch (Exception exception)
            {
                // Marshal the fault to the join in StopAsync: an exception escaping a raw
                // thread delegate would terminate the process.
                exited.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = GetType().Name,
        };

        _cancellationTokenSource = cancellationTokenSource;
        _exited = exited;

        thread.Start();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Signals cancellation to the executing work and waits for the dedicated thread to
    /// finish within the caller's shutdown budget.
    /// </summary>
    /// <param name="cancellationToken">Bounds the wait for the work to drain.</param>
    /// <returns>A task that completes once the work has finished.</returns>
    /// <exception cref="OperationCanceledException">
    /// The work did not finish before <paramref name="cancellationToken"/> was signaled.
    /// A cooperative <see cref="OperationCanceledException"/> exit of the work itself is a
    /// clean stop and does not throw; any other exception thrown by <see cref="Run"/> is
    /// rethrown here so the host observes it.
    /// </exception>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource? exited = _exited;

        // Stop called without start.
        if (exited is null)
        {
            return;
        }

        _cancellationTokenSource!.Cancel();

        try
        {
            // Join the work; a fault thrown by Run surfaces here.
            await exited.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Only clear run-state once the work actually finished; on a drain timeout the
            // thread is still running and a retried StopAsync must be able to rejoin it.
            if (exited.Task.IsCompleted)
            {
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;
                _exited = null;
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
