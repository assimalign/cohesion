using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal sealed class SqlBindingCaller
{
    private readonly TaskCompletionSource<Exception?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;

    internal SqlBindingCaller(Func<Task> action)
    {
        _thread = new Thread(() =>
        {
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
        });
        _thread.Start();
    }

    internal Task<Exception?> Completion => _completion.Task;

    internal bool WaitUntilBlocked()
        => SpinWait.SpinUntil(() => Completion.IsCompleted || (_thread.ThreadState & ThreadState.WaitSleepJoin) != 0,
            TimeSpan.FromSeconds(5));

    internal async Task<Exception?> JoinAsync()
    {
        var failure = await Completion.WaitAsync(TimeSpan.FromSeconds(10));
        _thread.Join();
        return failure;
    }
}
