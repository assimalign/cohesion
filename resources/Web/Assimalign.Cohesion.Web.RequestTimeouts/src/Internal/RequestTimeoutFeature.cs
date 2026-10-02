using System;
using System.Threading;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.RequestTimeouts.Internal;

/// <summary>
/// The per-exchange timeout engine and the <see cref="IRequestTimeoutFeature"/>
/// implementation. Owns two cancellation sources: the timeout source (armed and re-armed against
/// the composed <see cref="TimeProvider"/>) and a source linking it with the transport's
/// <see cref="IHttpContext.RequestCancelled"/> — the linked token is what downstream work
/// observes, so it trips on expiry <em>and</em> on a genuine request cancellation.
/// </summary>
/// <remarks>
/// The timeout source is created unarmed (an infinite due time) with the composed
/// <see cref="TimeProvider"/>, which binds every later <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>
/// to that provider, and is then armed once with the effective policy's interval. A handler's
/// <see cref="SetTimeout"/> can arm it even when no policy exists. Re-arming after the source has
/// fired is inherently a no-op (<see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> cannot
/// un-cancel), which is exactly the documented race semantic of <see cref="Disable"/>.
/// </remarks>
internal sealed class RequestTimeoutFeature : IRequestTimeoutFeature, IDisposable
{
    private readonly CancellationTokenSource _timeoutSource;
    private readonly CancellationTokenSource _linkedSource;
    private readonly RequestTimeoutPolicy? _policy;

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
        _timeoutSource = new CancellationTokenSource(Timeout.InfiniteTimeSpan, timeProvider);
        _linkedSource = CancellationTokenSource.CreateLinkedTokenSource(context.RequestCancelled, _timeoutSource.Token);
        _policy = policy;

        if (policy?.Timeout is { } timeout)
        {
            _timeoutSource.CancelAfter(timeout);
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
    public bool TimedOut => _timeoutSource.IsCancellationRequested;

    public void Disable() => _timeoutSource.CancelAfter(Timeout.InfiniteTimeSpan);

    public void SetTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "The request timeout must be a positive interval. Use Disable() to turn enforcement off.");
        }

        _timeoutSource.CancelAfter(timeout);
    }

    public void Dispose()
    {
        _linkedSource.Dispose();
        _timeoutSource.Dispose();
    }
}
