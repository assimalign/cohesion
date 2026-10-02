using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

/// <summary>
/// Runs the exchanges of one multiplexed (HTTP/2 or HTTP/3) connection concurrently and tracks
/// them, so the connection is disposed, and its concurrency slot released, only after the last of
/// them has finished.
/// </summary>
/// <remarks>
/// <para>
/// A countdown rather than a set of tasks. The count starts at one, the hold of the connection's
/// receive loop; it rises as each exchange starts and falls as each finishes, and the receive loop
/// gives up its hold in <see cref="WhenDrainedAsync"/> once it has stopped producing exchanges.
/// Whichever release reaches zero completes the drain, so the drain completes exactly once, after the
/// last exchange, with no lock and no per-exchange bookkeeping.
/// </para>
/// <para>
/// The tracker adds no queue: an exchange starts the moment the transport yields it. The number of
/// exchanges running at once is therefore bounded by what the transport admits — HTTP/2
/// <c>SETTINGS_MAX_CONCURRENT_STREAMS</c>, HTTP/3 QUIC stream credit.
/// </para>
/// </remarks>
internal sealed class MultiplexedExchangeTracker
{
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _outstanding = 1;
    private int _drainRequested;

    /// <summary>
    /// Starts serving one exchange on the thread pool and tracks it until it completes.
    /// </summary>
    /// <param name="serveExchange">
    /// Serves the exchange to completion. It is expected to isolate its own faults; a fault that
    /// escapes it anyway is observed here and does not affect the drain.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="serveExchange"/> is <see langword="null"/>.</exception>
    public void Start(Func<Task> serveExchange)
    {
        ArgumentNullException.ThrowIfNull(serveExchange);

        Interlocked.Increment(ref _outstanding);

        // RunAsync observes every outcome of the exchange and never faults, so the returned task
        // needs no observer of its own.
        _ = RunAsync(serveExchange);
    }

    /// <summary>
    /// Releases the receive loop's hold and returns a task that completes once every exchange started
    /// through <see cref="Start"/> has finished. Call it after the receive loop has stopped; repeated
    /// calls return the same task.
    /// </summary>
    /// <returns>A task that completes when the connection has no exchange left in flight. It never faults.</returns>
    public Task WhenDrainedAsync()
    {
        if (Interlocked.Exchange(ref _drainRequested, 1) == 0)
        {
            Release();
        }

        return _drained.Task;
    }

    private async Task RunAsync(Func<Task> serveExchange)
    {
        try
        {
            // Task.Run rather than a direct call: a pipeline may run synchronously for as long as it
            // likes before its first await, and on the receive loop's thread that would hold back
            // every sibling stream on the connection.
            await Task.Run(serveExchange).ConfigureAwait(false);
        }
        // Deviates from the repo "catch specific exceptions" rule per design decision: this is the
        // last guard of a fault-isolation boundary. Exchange bodies isolate their own faults, so
        // this only keeps a defect from surfacing as an unobserved task exception or a stuck drain.
        catch (Exception)
        {
        }
        finally
        {
            Release();
        }
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _outstanding) == 0)
        {
            _drained.TrySetResult();
        }
    }
}
