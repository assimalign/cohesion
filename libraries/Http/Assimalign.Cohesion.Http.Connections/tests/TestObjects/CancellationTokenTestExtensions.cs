using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// Waits on a <see cref="CancellationToken"/> the way the transport tests observe a connection or stream
/// closing: through its <c>ConnectionClosed</c> token, failing the test instead of hanging.
/// </summary>
internal static class CancellationTokenTestExtensions
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    extension(CancellationToken token)
    {
        /// <summary>
        /// Waits until the token is cancelled, failing with a <see cref="TimeoutException"/> after ten seconds.
        /// </summary>
        /// <returns>A task that completes once the token is cancelled.</returns>
        public Task WhenCancelledAsync()
        {
            TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration registration = token.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                cancelled);

            return WaitAsync(cancelled.Task, registration);
        }
    }

    private static async Task WaitAsync(Task cancelled, CancellationTokenRegistration registration)
    {
        using (registration)
        {
            await cancelled.WaitAsync(_timeout).ConfigureAwait(false);
        }
    }
}
