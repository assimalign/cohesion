using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Blob.Tests.TestObjects;

internal sealed class DeferredCrashOpen<T>
{
    private readonly T _result;
    private readonly bool _blocksSynchronously;
    private int _attempts;

    public DeferredCrashOpen(T result, bool blocksSynchronously)
    {
        _result = result;
        _blocksSynchronously = blocksSynchronously;
    }

    public IOException LastError { get; } = new("scanner retains blob.dat", unchecked((int)0x80070020));
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Attempts => Volatile.Read(ref _attempts);

    public async ValueTask<T> OpenAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _attempts) == 1)
        {
            throw LastError;
        }

        using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
        Entered.TrySetResult();
        // Deliberately ignore cancellation while held, including before returning the result.
        if (_blocksSynchronously)
        {
            Release.Task.GetAwaiter().GetResult();
        }
        else
        {
            await Release.Task.ConfigureAwait(false);
        }

        return _result;
    }
}

internal sealed class CrashOpenResult : IDisposable
{
    private int _disposeCount;

    public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public void Dispose()
    {
        Interlocked.Increment(ref _disposeCount);
        Disposed.TrySetResult();
    }
}

internal sealed class AsyncCrashOpenResult : IAsyncDisposable
{
    private int _disposeCount;

    public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        Disposing.TrySetResult();
        await ReleaseDisposal.Task.ConfigureAwait(false);
        Disposed.TrySetResult();
    }
}
