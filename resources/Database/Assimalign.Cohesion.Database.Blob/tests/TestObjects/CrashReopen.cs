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
        Exception? lastError = null;
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                try
                {
                    return await open(linked.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsFileContention(exception))
                {
                    // A killed process or scanner can retain a file handle past process exit.
                    // Only a successful open decides readiness; the delay merely paces retries.
                    lastError = exception;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (guard.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Crash recovery could not reopen '{file}' within the 30-second hang guard. Last error: {lastError}", lastError);
        }
    }

    private static bool IsFileContention(Exception exception)
    {
        if (exception is IOException && exception.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
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
