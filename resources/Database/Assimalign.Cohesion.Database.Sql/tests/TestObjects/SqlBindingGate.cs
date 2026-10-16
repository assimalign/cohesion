using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal sealed class SqlBindingGate : IDisposable
{
    private readonly ManualResetEventSlim _entered = new();
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
            _entered.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(15))) { throw new TimeoutException("Binding callback was not released."); }
            AfterRelease?.Invoke();
        }
        return value;
    }

    internal bool WaitUntilEntered() => _entered.Wait(TimeSpan.FromSeconds(5));
    internal void Release() => _release.Set();

    public void Dispose()
    {
        _entered.Dispose();
        _release.Dispose();
    }
}
