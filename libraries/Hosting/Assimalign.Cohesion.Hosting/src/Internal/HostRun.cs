using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Internal;

/// <summary>
/// One active <c>RunAsync</c> of a host: whether the host began stopping, and the outcome of that
/// stop, which the run joins when a direct <c>StopAsync</c> began it.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("HostRun, stopping = {HasBegunStopping}")]
internal sealed class HostRun
{
    private readonly TaskCompletionSource _stopCompletionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _stopException;
    private int _hasBegunStopping;

    internal bool HasBegunStopping => Volatile.Read(ref _hasBegunStopping) != 0;

    internal void BeginStopping()
    {
        Volatile.Write(ref _hasBegunStopping, 1);
    }

    internal void CompleteStop(Exception? exception)
    {
        Volatile.Write(ref _stopException, exception);
        _stopCompletionSource.TrySetResult();
    }

    internal async Task WaitForStopAsync()
    {
        await _stopCompletionSource.Task.ConfigureAwait(false);

        Exception? exception = Volatile.Read(ref _stopException);
        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
