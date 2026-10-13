using System;
using System.Threading;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.RequestTimeouts.Internal;

/// <summary>
/// The per-exchange timeout engine and the <see cref="IRequestTimeoutFeature"/>
/// implementation. Owns a timer and two cancellation sources: the timeout source and a source
/// linking it with the transport's
/// <see cref="IHttpContext.RequestCancelled"/> — the linked token is what downstream work
/// observes, so it trips on expiry <em>and</em> on a genuine request cancellation.
/// </summary>
/// <remarks>
/// The timer uses the composed <see cref="TimeProvider"/>. Re-arming and expiry share a lock so
/// the recorded interval always belongs to the arm that expired. A queued callback from an older
/// arm checks the current deadline. No diagnostic event is raised from this callback.
/// </remarks>
internal sealed class RequestTimeoutFeature : IRequestTimeoutFeature, IDisposable
{
    private readonly CancellationTokenSource _timeoutSource;
    private readonly CancellationTokenSource _linkedSource;
    private readonly RequestTimeoutPolicy? _policy;
    private readonly Lock _timerLock = new();
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _timer;
    private long _armedTimeoutTicks;
    private long _armedTimestamp;
    private bool _armed;
    private bool _timedOut;
    private bool _disposed;

    /// <summary>
    /// Creates the timeout engine for one exchange and arms it with <paramref name="policy"/>.
    /// </summary>
    /// <param name="context">The exchange whose request token the linked token follows.</param>
    /// <param name="policy">
    /// The effective policy: the published endpoint's policy when it carries one (which replaces the
    /// global default outright, a disabled policy included), otherwise the global default;
    /// <see langword="null"/> when neither exists.
    /// </param>
    /// <param name="timeProvider">The time source the timer measures against.</param>
    public RequestTimeoutFeature(IHttpContext context, RequestTimeoutPolicy? policy, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _timeoutSource = new CancellationTokenSource();
        _linkedSource = CancellationTokenSource.CreateLinkedTokenSource(context.RequestCancelled, _timeoutSource.Token);
        _policy = policy;
        _timer = timeProvider.CreateTimer(static state => ((RequestTimeoutFeature)state!).Expire(),
            this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        if (policy?.Timeout is { } timeout)
        {
            SetTimeout(timeout);
        }
    }

    public string Name => nameof(IRequestTimeoutFeature);

    public CancellationToken Token => _linkedSource.Token;

    /// <summary>
    /// The policy in effect for the exchange: the endpoint policy when the published endpoint carries
    /// one, otherwise the global default; <see langword="null"/> when neither exists.
    /// </summary>
    public RequestTimeoutPolicy? EffectivePolicy => _policy;

    /// <summary>
    /// Whether the timeout timer has fired. Distinguishes an expiry from a genuine request
    /// cancellation when both surface as an <see cref="OperationCanceledException"/> — the
    /// middleware additionally checks that <see cref="IHttpContext.RequestCancelled"/> itself is
    /// not cancelled before attributing the unwind to the timeout.
    /// </summary>
    public bool TimedOut => Volatile.Read(ref _timedOut);

    // Retain the interval after expiry, including when downstream tries to re-arm a fired source.
    internal TimeSpan ArmedTimeout => TimeSpan.FromTicks(Interlocked.Read(ref _armedTimeoutTicks));

    public void Disable()
    {
        lock (_timerLock)
        {
            _armed = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    public void SetTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                ErrorMessages.RequestTimeoutInvalid);
        }

        // Match CancelAfter's range validation even when the timer has already fired.
        if ((long)timeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        lock (_timerLock)
        {
            if (!_timedOut && !_disposed)
            {
                Interlocked.Exchange(ref _armedTimeoutTicks, timeout.Ticks);
                _armedTimestamp = _timeProvider.GetTimestamp();
                _armed = true;
                _timer.Change(timeout, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Expire()
    {
        lock (_timerLock)
        {
            if (!_armed || _timedOut || _disposed)
            {
                return;
            }

            TimeSpan remaining = ArmedTimeout - _timeProvider.GetElapsedTime(_armedTimestamp);
            if (remaining > TimeSpan.Zero)
            {
                // An old callback may already be queued when SetTimeout re-arms the timer.
                // Also tolerate providers that round sub-millisecond intervals down.
                _timer.Change(remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining,
                    Timeout.InfiniteTimeSpan);
                return;
            }

            _armed = false;
            Volatile.Write(ref _timedOut, true);
        }

        // Cancellation can synchronously run arbitrary downstream callbacks; never hold the lock.
        try
        {
            _timeoutSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The request may finish and dispose its sources after the timer claimed expiry.
        }
    }

    public void Dispose()
    {
        lock (_timerLock)
        {
            _disposed = true;
            _armed = false;
            _timer.Dispose();
        }

        _linkedSource.Dispose();
        _timeoutSource.Dispose();
    }
}
