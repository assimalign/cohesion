using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Dispatches calibration and verification through one frame on one fixed-stack thread.</summary>
internal sealed class SqlDepthTestThread : IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;

    internal SqlDepthTestThread(int stackKilobytes)
    {
        _thread = new Thread(Dispatch, maxStackSize: stackKilobytes * 1024);
        _thread.Start();
    }

    internal (T? Result, Exception? Failure) Run<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<(T? Result, Exception? Failure)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try
            {
                completion.SetResult((work(), null));
            }
            catch (Exception failure)
            {
                // Stack probes intentionally report failures to the calibrating test.
                completion.SetResult((default, failure));
            }
        });
        return completion.Task.GetAwaiter().GetResult();
    }

    private void Dispatch()
    {
        foreach (var work in _work.GetConsumingEnumerable())
        {
            work();
        }
    }

    public void Dispose()
    {
        _work.CompleteAdding();
        _thread.Join();
        _work.Dispose();
    }
}
