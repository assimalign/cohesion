using System;
using System.Threading;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// A cancellation source that fires when no read or write has completed for a set time, and when the
/// caller or the client gives up. Each completed operation calls <see cref="Restart"/>.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("ActivityTimeout {Timeout}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ActivityTimeout : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly CancellationToken _callerToken;
    private readonly CancellationToken _requestToken;

    public ActivityTimeout(TimeSpan timeout, CancellationToken callerToken, CancellationToken requestToken)
    {
        _source = CancellationTokenSource.CreateLinkedTokenSource(callerToken, requestToken);
        _callerToken = callerToken;
        _requestToken = requestToken;
        Timeout = timeout;
        Restart();
    }

    /// <summary>
    /// Gets the idle time after which <see cref="Token"/> is canceled.
    /// </summary>
    public TimeSpan Timeout { get; }

    /// <summary>
    /// Gets the token every operation of the exchange observes.
    /// </summary>
    public CancellationToken Token => _source.Token;

    /// <summary>
    /// Gets a value indicating whether the caller's token or the client's abort canceled the token.
    /// </summary>
    public bool IsCanceledByCaller => _callerToken.IsCancellationRequested || _requestToken.IsCancellationRequested;

    /// <summary>
    /// Gets a value indicating whether the idle time ran out.
    /// </summary>
    public bool HasTimedOut => _source.IsCancellationRequested && !IsCanceledByCaller;

    /// <summary>
    /// Restarts the idle time after an operation completed.
    /// </summary>
    public void Restart()
    {
        if (Timeout == System.Threading.Timeout.InfiniteTimeSpan)
        {
            return;
        }

        try
        {
            _source.CancelAfter(Timeout);
        }
        catch (ObjectDisposedException)
        {
            // A relay direction that finishes after the exchange disposed the source has nothing to time.
        }
    }

    public void Dispose()
    {
        _source.Dispose();
    }

    private sealed class DebugView
    {
        private readonly ActivityTimeout _target;

        public DebugView(ActivityTimeout value)
        {
            _target = value;
        }

        public TimeSpan Timeout => _target.Timeout;
        public bool IsCanceledByCaller => _target.IsCanceledByCaller;
        public bool HasTimedOut => _target.HasTimedOut;
    }
}
