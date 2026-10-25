using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Client.Tests;

/// <summary>Stops the first captured context entry before its queued delegate starts.</summary>
internal sealed class CancellationDispatchGate : IDisposable
{
    private readonly AsyncLocal<bool> _capture;
    private readonly ManualResetEventSlim _release = new(false);
    private int _entered;
    private int _timedOut;

    internal CancellationDispatchGate()
    {
        _capture = new AsyncLocal<bool>(OnContextChanged);
    }

    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool TimedOut => Volatile.Read(ref _timedOut) != 0;

    internal void EnableCapture() => _capture.Value = true;
    internal void DisableCapture() => _capture.Value = false;
    internal void Release() => _release.Set();

    public void Dispose()
    {
        DisableCapture();
        Release();
        _release.Dispose();
    }

    private void OnContextChanged(AsyncLocalValueChangedArgs<bool> change)
    {
        if (!change.ThreadContextChanged || !change.CurrentValue ||
            Interlocked.CompareExchange(ref _entered, 1, 0) != 0)
        {
            return;
        }
        Entered.TrySetResult();
        // An exception here could fail the process during ExecutionContext restoration.
        // Record a hang-guard expiry for the test instead of throwing from this callback.
        try
        {
            if (!_release.Wait(TimeSpan.FromSeconds(30)))
            {
                Volatile.Write(ref _timedOut, 1);
            }
        }
        catch (ObjectDisposedException)
        {
            // A delayed captured context can arrive after a failed test has disposed the gate.
            // Keep that cleanup path nonthrowing during ExecutionContext restoration too.
            Volatile.Write(ref _timedOut, 1);
        }
    }
}
