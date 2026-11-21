using System.Diagnostics;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Lets callers wait for a condition that the protocol engine signals, such as credit or window space
/// arriving. Waiters re-check their condition when woken.
/// </summary>
/// <remarks>
/// Not thread-safe: every member is called under the connection's lock. Continuations run
/// asynchronously, so signalling never runs a waiter's code under that lock.
/// </remarks>
[DebuggerDisplay("IsWaiting = {IsWaiting}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class AmqpWaiter
{
    private TaskCompletionSource? _source;

    /// <summary>
    /// Gets a value indicating whether anyone waits.
    /// </summary>
    public bool IsWaiting => _source is not null;

    /// <summary>
    /// Returns a task that completes at the next <see cref="Signal"/>.
    /// </summary>
    public Task WaitAsync()
    {
        return (_source ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }

    /// <summary>
    /// Wakes every waiter.
    /// </summary>
    public void Signal()
    {
        TaskCompletionSource? source = _source;

        _source = null;
        source?.TrySetResult();
    }

    private sealed class DebugView
    {
        private readonly AmqpWaiter _waiter;

        public DebugView(AmqpWaiter waiter)
        {
            _waiter = waiter;
        }

        public bool IsWaiting => _waiter.IsWaiting;
    }
}
