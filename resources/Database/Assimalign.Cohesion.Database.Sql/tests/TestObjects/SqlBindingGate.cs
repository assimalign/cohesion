using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal sealed class SqlBindingGate : IDisposable
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _release = new();
    private int _armed;
    private int _calls;

    internal int Calls => Volatile.Read(ref _calls);
    internal Action? AfterRelease { get; set; }

    internal void Arm()
    {
        _calls = 0;
        _armed = 1;
    }

    internal int Invoke(int value)
    {
        Interlocked.Increment(ref _calls);
        if (Interlocked.Exchange(ref _armed, 0) != 0)
        {
            _entered.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(30))) { throw new TimeoutException("Binding callback was not released."); }
            AfterRelease?.Invoke();
        }
        return value;
    }

    internal Task WaitUntilEnteredAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
    internal void Release() => _release.Set();

    public void Dispose()
    {
        _release.Dispose();
    }
}
