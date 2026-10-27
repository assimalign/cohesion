using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// Deviates from the repo RootNamespace rule: this test-only source is linked into crash tests
// and verifier fixtures, keeping one stateless reopen policy without a shipped API (#1713).
namespace Assimalign.Cohesion.Database.Blob.Tests.TestObjects;

internal static class CrashReopen
{
    internal static async ValueTask<T> OpenAsync<T>(
        string file,
        Func<CancellationToken, ValueTask<T>> open,
        CancellationToken cancellationToken = default)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, guard.Token);
        CancellationToken attemptToken = linked.Token;
        Exception? lastError = null;
        try
        {
            while (true)
            {
                attemptToken.ThrowIfCancellationRequested();
                // Even a delegate that blocks before returning its ValueTask must not hold
                // the caller. Do not cancel the task itself: we must observe any late result.
                Task<T> attempt = Task.Run(async () =>
                {
                    attemptToken.ThrowIfCancellationRequested();
                    return await open(attemptToken).ConfigureAwait(false);
                });
                try
                {
                    T result = await attempt.WaitAsync(attemptToken).ConfigureAwait(false);
                    attemptToken.ThrowIfCancellationRequested();
                    return result;
                }
                catch (OperationCanceledException) when (attemptToken.IsCancellationRequested)
                {
                    // Ownership transfers to this observer before the caller leaves. It also
                    // handles completion racing cancellation after WaitAsync returned a value.
                    _ = Task.Run(() => ObserveLateAttemptAsync(attempt));
                    throw;
                }
                catch (Exception exception) when (IsFileContention(exception))
                {
                    // A killed process or scanner can retain a file handle past process exit.
                    // Only a successful open decides readiness; the delay merely paces retries.
                    lastError = exception;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), attemptToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (attemptToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"Crash recovery could not reopen '{file}' within the 30-second hang guard. Last error: {lastError}", lastError);
        }
    }

    private static async Task ObserveLateAttemptAsync<T>(Task<T> attempt)
    {
        try
        {
            T result = await attempt.ConfigureAwait(false);
            if (result is IAsyncDisposable asynchronous)
            {
                await asynchronous.DisposeAsync().ConfigureAwait(false);
            }
            else if (result is IDisposable synchronous)
            {
                synchronous.Dispose();
            }
        }
        catch (Exception)
        {
            // Observe late open/disposal failures. The caller has already timed out or
            // cancelled, so cleanup cannot replace its outcome or fault an unobserved task.
        }
    }

    private static bool IsFileContention(Exception exception)
    {
        // .NET's Unix FileShare emulation reports EWOULDBLOCK/EAGAIN as the raw errno
        // in IOException.HResult: 11 on Linux, 35 on macOS. Other errnos must not retry.
        if (exception is IOException &&
            (exception.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) ||
             (OperatingSystem.IsLinux() && exception.HResult == 11) ||
             (OperatingSystem.IsMacOS() && exception.HResult == 35)))
        {
            return true;
        }

        // Do not hide a second, unrelated failure in an aggregate.
        if (exception is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsFileContention);
        }

        return exception.InnerException is { } inner && IsFileContention(inner);
    }
}
