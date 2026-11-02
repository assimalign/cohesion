using System;
using System.Threading;

namespace Assimalign.Cohesion.Hosting.Tests;

/// <summary>
/// Stands in for the process's shutdown signals: counts the runs that register for them and lets a
/// test raise one, as the platform's SIGTERM would.
/// </summary>
internal sealed class TestShutdownSignals
{
    private Action? _requestShutdown;
    private int _registrations;
    private int _disposals;

    public int Registrations => Volatile.Read(ref _registrations);

    public int Disposals => Volatile.Read(ref _disposals);

    public IDisposable Register(Action requestShutdown)
    {
        Interlocked.Increment(ref _registrations);
        Volatile.Write(ref _requestShutdown, requestShutdown);
        return new Registration(this);
    }

    public void Raise()
    {
        (Volatile.Read(ref _requestShutdown) ?? throw new InvalidOperationException("No run registered for shutdown signals.")).Invoke();
    }

    private sealed class Registration : IDisposable
    {
        private readonly TestShutdownSignals _signals;

        public Registration(TestShutdownSignals signals)
        {
            _signals = signals;
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _signals._disposals);
        }
    }
}
