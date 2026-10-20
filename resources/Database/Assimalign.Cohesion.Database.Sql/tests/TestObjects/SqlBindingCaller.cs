using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal sealed class SqlBindingCaller
{
    private readonly TaskCompletionSource<Exception?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;

    internal SqlBindingCaller(Func<Task> action)
    {
        _thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new WaitObserver(_blocked));
            try
            {
                action().GetAwaiter().GetResult();
                _completion.SetResult(null);
            }
            catch (Exception failure)
            {
                // The test inspects the original failure after joining every caller.
                _completion.SetResult(failure);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }
        });
        _thread.Start();
    }

    internal Task<Exception?> Completion => _completion.Task;

    internal async Task<bool> WaitUntilBlockedAsync()
    {
        await Task.WhenAny(_blocked.Task, Completion).WaitAsync(TimeSpan.FromSeconds(30));
        return _blocked.Task.IsCompleted;
    }

    internal async Task<Exception?> JoinAsync()
    {
        var failure = await Completion.WaitAsync(TimeSpan.FromSeconds(30));
        _thread.Join();
        return failure;
    }

    // The CLR notifies this context when the caller enters a blocking managed wait.
    // Signal that transition directly instead of sampling ThreadState on a deadline.
    private sealed class WaitObserver : SynchronizationContext
    {
        private readonly TaskCompletionSource _blocked;

        internal WaitObserver(TaskCompletionSource blocked)
        {
            _blocked = blocked;
            SetWaitNotificationRequired();
        }

        public override int Wait(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout)
        {
            _blocked.TrySetResult();
            return base.Wait(waitHandles, waitAll, millisecondsTimeout);
        }
    }
}
